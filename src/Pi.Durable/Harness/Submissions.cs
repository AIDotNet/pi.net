using Pi.Chord.Context;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using TxDocChange = Pi.Chord.Delta.Tracker<IReadOnlyDictionary<string, object?>>.Change;

/// <summary>
/// 一个 Harness 的持久提交的受理、等待与撤回。对应 TS <c>harness/submissions.ts</c> 全量。
/// </summary>
public sealed class Submissions
{
    private readonly DurableSession _session;
    private readonly IStorage _storage;
    private readonly Func<long> _now;
    private readonly Func<QueueMode> _steeringMode;
    private readonly Func<QueueMode> _followUpMode;
    private readonly Action _resume;
    private readonly Waiters<SubmissionId, SettledSubmissionRecord> _waiters = new();
    private volatile bool _closed;

    public Submissions(
        DurableSession session, IStorage storage, Func<long> now,
        Func<QueueMode> steeringMode, Func<QueueMode> followUpMode, Action resume)
    {
        _session = session;
        _storage = storage;
        _now = now;
        _steeringMode = steeringMode;
        _followUpMode = followUpMode;
        _resume = resume;
        session.SubscribeCommits(Observe);
        session.SubscribeClose(() =>
        {
            _closed = true;
            _waiters.RejectAll(new InvalidOperationException("Harness is closed"));
        });
    }

    /// <summary>一次提交内受理；见 <see cref="AdmitSubmissionAsync"/>。</summary>
    public async Task<ISubmission> SubmitAsync(ConversationId conversationId, SubmissionDraft draft, Context context)
    {
        _resume();
        var id = await _session.CommitWithAsync(
            tx => Admission.AdmitSubmissionAsync(tx, conversationId, draft, _now(), _steeringMode(), _followUpMode()),
            context).ConfigureAwait(false);
        return new SubmissionHandle(id, this);
    }

    /// <summary>
    /// 在调用方的提交 <paramref name="tx"/> 内受理一次提交，规则与 <see cref="SubmitAsync(ConversationId, SubmissionDraft, Context)"/>
    /// 相同，返回它的 ID：这次受理随该提交一起存储，或者一起丢掉。必须在这次提交的首次表写（<c>AppendEntryAsync</c>、
    /// 创建任务/提交、另一次受理）之前调用；任何路径上晚于它都会抛 <see cref="ReadAfterWrite"/>。文档在它前后读写都
    /// 可以。拒绝（<see cref="ConversationBusy"/>、<see cref="ConversationIdle"/>）在写之前抛出，回调可以捕获。
    /// 别在回调里调 <see cref="GetAsync"/> 等 Harness 读：它们会等持有这条线的提交。事务属于别的 Session 时抛错。
    /// 对应 TS <c>Submissions.admit(tx, …)</c>。
    /// </summary>
    public async Task<SubmissionId> AdmitInCommitAsync(
        ITx tx, ConversationId conversationId, SubmissionDraft draft)
    {
        // 品牌检查：只有本 Session 自己造的事务算数，别的 Session 的、或被包装过的都不是。
        if (tx is not Transaction transaction || !_session.OwnsTransaction(transaction))
        {
            throw new InvalidOperationException("The transaction belongs to another Session");
        }

        // 每条路径都强制：受理只在部分路径读表（请求 ID、空闲边界、steer 的 trace）。
        if (transaction.HasTableWrite) throw new ReadAfterWrite("submit(tx, draft)");
        // 这次受理起的运行需要进程：与 submit() 一样请求进度（不读表，只让调度器下一遍看到它）。
        _resume();
        return await Admission.AdmitSubmissionAsync(
            transaction, conversationId, draft, _now(), _steeringMode(), _followUpMode()).ConfigureAwait(false);
    }

    /// <summary>既有提交的句柄；不存在为 null。</summary>
    public async Task<ISubmission?> GetAsync(SubmissionId id, Context context)
    {
        var record = await _session.ReadOnLineAsync(() => _storage.GetSubmissionAsync(id)).ConfigureAwait(false);
        return record is null ? null : new SubmissionHandle(record.Id, this);
    }

    public async Task<SubmissionRecord> StatusAsync(SubmissionId id, Context context)
    {
        var record = await _session.ReadOnLineAsync(() => _storage.GetSubmissionAsync(id)).ConfigureAwait(false);
        return record ?? throw new InvalidOperationException($"Submission {id.Value} does not exist");
    }

    public async Task<SettledSubmissionRecord> WaitAsync(SubmissionId id, Context context)
    {
        _resume();
        // 在线上检查并注册，使结算发布不落在两者之间。
        var found = await _session.ReadOnLineAsync(async () =>
        {
            var record = await _storage.GetSubmissionAsync(id).ConfigureAwait(false);
            if (record is null) throw new InvalidOperationException($"Submission {id.Value} does not exist");
            if (IsSettled(record)) return Task.FromResult(new SettledSubmissionRecord(record));
            // 关闭同步拒绝已注册等待者，并可能在读取期间开始。
            if (_closed) throw new InvalidOperationException("Harness is closed");
            return _waiters.Add(id, context);
        }).ConfigureAwait(false);
        return await found.ConfigureAwait(false);
    }

    /// <summary>撤回排队提交并移除其收件箱项；已放置输入与已结算提交按状态报告。返回 "aborted" / "already_placed" / "settled" / "not_found"。</summary>
    public Task<string> AbortAsync(SubmissionId id, Context context, ConversationId? conversationId = null)
        => _session.CommitWithAsync(async tx =>
        {
            var record = await tx.GetSubmissionAsync(id).ConfigureAwait(false);
            if (record is null || (conversationId is { } conv && record.ConversationId != conv)) return "not_found";
            var status = record switch
            {
                SubmissionRecord.InputRecord input => input.Status,
                SubmissionRecord.ContinueRecord continuation => continuation.Status,
                SubmissionRecord.WriteRecord write => write.Status,
                _ => (SubmissionStatus?)null,
            };
            if (status == SubmissionStatus.Queued)
            {
                tx.SettleSubmission(id, new SubmissionSettlement.Unanswered("aborted"));
                await Inbox.RemoveInboxItemAsync(tx, record.ConversationId, id).ConfigureAwait(false);
                return "aborted";
            }

            return status == SubmissionStatus.Placed ? "already_placed" : "settled";
        }, context);

    private void Observe(CommitPublication publication, Context context)
    {
        foreach (var change in publication.Changes)
        {
            if (change is CommitChange.SubmissionTable { Value: { } record } && IsSettled(record))
            {
                _waiters.Resolve(record.Id, new SettledSubmissionRecord(record));
            }
        }
    }

    internal static bool IsSettled(SubmissionRecord record)
        => record switch
        {
            SubmissionRecord.InputRecord input => IsTerminal(input.Status),
            SubmissionRecord.WriteRecord write => IsTerminal(write.Status),
            SubmissionRecord.ContinueRecord continuation => IsTerminal(continuation.Status),
            _ => false,
        };

    private static bool IsTerminal(SubmissionStatus status) => status is SubmissionStatus.Done or SubmissionStatus.Unanswered;

    private sealed class SubmissionHandle(SubmissionId id, Submissions submissions) : ISubmission
    {
        public SubmissionId Id => id;

        public Task<SubmissionRecord> StatusAsync(Context context) => submissions.StatusAsync(id, context);

        public Task<SettledSubmissionRecord> WaitAsync(Context context) => submissions.WaitAsync(id, context);

        public async Task<string> AbortAsync(Context context)
        {
            var result = await submissions.AbortAsync(id, context).ConfigureAwait(false);
            if (result == "not_found") throw new InvalidOperationException($"Submission {id.Value} does not exist");
            return result;
        }
    }
}

/// <summary>
/// 在提交内受理（spec §6）；<c>Conversation.submit()</c> 与对话所有的压缩共用。已知请求 ID 直接返回既有提交。
/// 忙碌对话把输入排队进 <c>pi.inbox</c>，或对 <c>whenBusy: "reject"</c> 拒绝；空闲但已有排队项的对话排在它们之后
/// 并运行一次 final 边界。否则空闲输入放置用户条目并启动运行，空闲写入追加条目并结算 done（head 落到活动范围
/// 之前时 stale）。对应 TS <c>admitSubmission()</c>。
/// </summary>
public static class Admission
{
    public static async Task<SubmissionId> AdmitSubmissionAsync(
        Transaction tx, ConversationId conversationId, SubmissionDraft draft, long now,
        QueueMode steeringMode, QueueMode followUpMode)
    {
        if (draft.RequestId is { } requestId)
        {
            var existing = await tx.GetSubmissionByRequestAsync(conversationId, requestId).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Type != draft.Type)
                {
                    throw new InvalidOperationException(
                        $"Request {requestId} already identifies a submission of type {existing.Type}");
                }

                return existing.Id;
            }
        }

        var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
        var busy = live.Draft.ContainsKey("run");
        if (busy && draft is SubmissionDraft.Input { WhenBusy: "reject" })
        {
            throw new ConversationBusy(conversationId);
        }

        // 空闲拒绝发生在任何写之前：这条输入不会启动一次运行。
        if (!busy && draft is SubmissionDraft.Input { WhenIdle: "reject" })
        {
            throw new ConversationIdle(conversationId);
        }

        if (draft is SubmissionDraft.Continue)
        {
            // 从不排队：能不能续跑取决于当前运行怎么结束。因此在任何写之前就拒绝忙碌的。
            if (busy) throw new ConversationBusy(conversationId);
            var continueBoundary = await Inbox.PrepareBoundaryAsync(
                tx, conversationId, steeringMode, followUpMode).ConfigureAwait(false);
            var admittedContinue = await tx.CreateSubmissionAsync(new SubmissionCreate.ContinueCreate
            {
                ConversationId = conversationId,
                RequestId = draft.RequestId,
                Status = SubmissionStatus.Placed,
            }).ConfigureAwait(false);
            // 上一次失败运行按 afterFailedRun: "keep" 留在队列里的项先被放置，并一起并入这次运行。
            var appliedContinue = await Inbox.ApplyBoundaryAsync(tx, continueBoundary, BoundaryAt.Final, now)
                .ConfigureAwait(false);
            // continue 自己不落条目：运行第一个条目是失败运行记下的那个（接着看它的历史），否则是它放置的
            // 第一个排队项，再没有则以首个工具轮答案补。必须在 StartRunAsync 清掉 runFailure 之前读。
            var continuedFirst = Live.RunFailureFirst(live) ?? appliedContinue.First;
            await Generation.StartRunAsync(
                tx, conversationId, live, [.. appliedContinue.Users, admittedContinue.Id],
                first: continuedFirst).ConfigureAwait(false);
            return admittedContinue.Id;
        }

        // 边界读表，因此它在首次表写之前准备；忙碌的无需边界。
        var boundary = busy
            ? null
            : await Inbox.PrepareBoundaryAsync(tx, conversationId, steeringMode, followUpMode).ConfigureAwait(false);
        var items = boundary is not null
            ? InboxItems(boundary.Inbox)
            : await InboxItemsAsync(tx, conversationId).ConfigureAwait(false);
        if (boundary is null || items.Count > 0)
        {
            var created = await tx.CreateSubmissionAsync(CreateFor(draft, conversationId, SubmissionStatus.Queued))
                .ConfigureAwait(false);
            // 忙碌路径无边界，需读取收件箱文档作为草稿目标（TS：boundary?.inbox ?? await tx.doc(InboxDoc, …)）。
            var inbox = boundary?.Inbox ?? await tx.DocAsync(Inbox.InboxDoc, conversationId).ConfigureAwait(false);
            if (draft is SubmissionDraft.Write write)
            {
                Inbox.AppendItem(inbox, new Dictionary<string, object?>
                {
                    ["id"] = created.Id.Value,
                    ["mode"] = "write",
                    ["entry"] = JsonTrees.ToTree(write.Entry),
                });
            }
            else
            {
                var input = (SubmissionDraft.Input)draft;
                Inbox.AppendItem(inbox, new Dictionary<string, object?>
                {
                    ["id"] = created.Id.Value,
                    ["mode"] = input.WhenBusy == "steer" ? "steer" : "followUp",
                    ["content"] = JsonTrees.ToTree(input.Content),
                });
            }

            if (boundary is null) return created.Id;
            var applied = await Inbox.ApplyBoundaryAsync(tx, boundary, BoundaryAt.Final, now).ConfigureAwait(false);
            if (applied.Users.Count > 0)
            {
                await Generation.StartRunAsync(tx, conversationId, live, applied.Users, first: applied.First)
                    .ConfigureAwait(false);
            }

            return created.Id;
        }

        if (draft is SubmissionDraft.Write writeDraft)
        {
            if (Inbox.IsStale(boundary, writeDraft.Entry))
            {
                var stale = await tx.CreateSubmissionAsync(new SubmissionCreate.WriteCreate
                {
                    ConversationId = conversationId,
                    RequestId = draft.RequestId,
                    Status = SubmissionStatus.Unanswered,
                    Reason = "stale",
                }).ConfigureAwait(false);
                return stale.Id;
            }

            var entry = await tx.AppendEntryAsync(conversationId, writeDraft.Entry).ConfigureAwait(false);
            var done = await tx.CreateSubmissionAsync(new SubmissionCreate.WriteCreate
            {
                ConversationId = conversationId,
                RequestId = draft.RequestId,
                Status = SubmissionStatus.Done,
                Entry = entry.Id,
            }).ConfigureAwait(false);
            return done.Id;
        }

        var inputDraft = (SubmissionDraft.Input)draft;
        var placed = await tx.AppendEntryAsync(
            DurableEntries.UserEntry,
            conversationId,
            new TypedEntryDraft<object?> { Model = [new Pi.Ai.Types.UserMessage(inputDraft.Content, now)] })
            .ConfigureAwait(false);
        var admitted = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
        {
            ConversationId = conversationId,
            RequestId = draft.RequestId,
            Status = SubmissionStatus.Placed,
            Entry = placed.Id,
        }).ConfigureAwait(false);
        // 空闲输入放置了它自己的条目：那就是运行的第一个条目。
        await Generation.StartRunAsync(tx, conversationId, live, [admitted.Id], first: placed.Id)
            .ConfigureAwait(false);
        return admitted.Id;
    }


    internal static SubmissionCreate CreateFor(SubmissionDraft draft, ConversationId conversationId, SubmissionStatus status)
        => draft switch
        {
            SubmissionDraft.Input input => new SubmissionCreate.InputCreate
            {
                ConversationId = conversationId,
                RequestId = draft.RequestId,
                Status = status,
            },
            SubmissionDraft.Continue => new SubmissionCreate.ContinueCreate
            {
                ConversationId = conversationId,
                RequestId = draft.RequestId,
                Status = status,
            },
            SubmissionDraft.Write write => new SubmissionCreate.WriteCreate
            {
                ConversationId = conversationId,
                RequestId = draft.RequestId,
                Status = status,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(draft)),
        };

    private static List<IReadOnlyDictionary<string, object?>> InboxItems(TxDocChange inbox)
        => inbox.Draft.TryGetValue("items", out var value) && value is IReadOnlyList<object?> list
            ? [.. list.OfType<IReadOnlyDictionary<string, object?>>()]
            : [];

    private static async Task<List<IReadOnlyDictionary<string, object?>>> InboxItemsAsync(
        Transaction tx, ConversationId conversationId)
    {
        var inbox = await tx.DocAsync(Inbox.InboxDoc, conversationId).ConfigureAwait(false);
        return InboxItems(inbox);
    }
}
