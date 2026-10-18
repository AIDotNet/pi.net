namespace Pi.CodingAgent.Cli.Experimental;

// ============================================================================
// 实验性子命令的迷你命令框架 — TS `cli/experimental/command.ts`（225 行）。
//
// 这是一套「选项表 + 子命令表 + builder + action」的小框架，和稳定 CLI 的
// `cli/args.ts` 完全独立（选项前缀是 `-x`/`--xxx`，不认位置参数，遇到不认识的
// 参数就整体落到 remainingArgs 里交给上层报错）。
//
// 类型建模差异（TS 是泛型 + 交叉/联合类型，C# 只能落地等价形状）：
//   - `CommandParseResult<TInvocation>` / `CommandExecutionResult<TInvocation>` 的
//     `command` 字段在 TS 里是子命令联合类型；C# 统一收敛到
//     <see cref="ICommandInvocation"/>，具体类型由调用方按 `Command` 名 switch 回来。
//   - `Command<TOwn, TContext, TInvocation>` 的三参数在 C# 收成两参数
//     `Command<TInvocation, TContext>`：第三个参数只用于让 `.command(...)` 返回
//     「并集」类型，C# 里 `Command` 返回自身即可，并集由 <see cref="ICommandInvocation"/> 承载。
//   - `CommandOptionParseResult<T>` 保留泛型（它的 `value` 是有类型的）。
//   - `CommandOption<T>` 的 `parse` 在注册表里被类型擦除成 `object?`，与 TS 的
//     `CommandOption<unknown>` 一致；取值时由泛型 `Value/Values` 还原。
//   - `action` 的 `void | Promise<void>` → `Func<TInvocation, TContext, Task>`。
// ============================================================================

/// <summary>对应 TS <c>NamedCommandInvocation</c>：每个子命令结果都带一个 <c>command</c> 名。</summary>
public interface ICommandInvocation
{
    string Command { get; }
}

/// <summary>对应 TS <c>CommandOptionParseResult&lt;TValue&gt;</c>。</summary>
public abstract record CommandOptionParseResult<TValue>
{
    /// <summary>对应 TS <c>{ ok: true; value }</c>。</summary>
    public sealed record Ok(TValue Value) : CommandOptionParseResult<TValue>;

    /// <summary>对应 TS <c>{ ok: false; error }</c>。</summary>
    public sealed record Failed(string Error) : CommandOptionParseResult<TValue>;
}

/// <summary>选项注册表里的类型擦除视图（TS 的 <c>CommandOption&lt;unknown&gt;</c>）。</summary>
public interface ICommandOption
{
    string Name { get; }

    /// <summary>TS <c>flag</c>：是开关而非取值选项。</summary>
    bool IsFlag { get; }

    /// <summary>TS <c>repeatable</c>：允许出现多次（否则第二次报错）。</summary>
    bool Repeatable { get; }

    /// <summary>擦除类型后的解析入口。</summary>
    CommandOptionParseResult<object?> ParseValue(string value);
}

/// <summary>对应 TS <c>CommandOption&lt;TValue&gt;</c>。</summary>
public sealed class CommandOption<TValue> : ICommandOption
{
    public required string Name { get; init; }

    public bool IsFlag { get; init; }

    public bool Repeatable { get; init; }

    public required Func<string, CommandOptionParseResult<TValue>> Parse { get; init; }

    public CommandOptionParseResult<object?> ParseValue(string value) => Parse(value) switch
    {
        CommandOptionParseResult<TValue>.Ok ok => new CommandOptionParseResult<object?>.Ok(ok.Value),
        CommandOptionParseResult<TValue>.Failed failed => new CommandOptionParseResult<object?>.Failed(failed.Error),
        _ => throw new InvalidOperationException($"Unexpected option parse result for {Name}"),
    };
}

/// <summary>对应 TS <c>ParsedCommandInput</c>：builder 拿到的只读视图。</summary>
public interface IParsedCommandInput
{
    IReadOnlyList<string> RemainingArgs { get; }

    /// <summary>TS <c>value</c>：取首个值；未提供时返回 <c>default</c>（引用类型即 null）。</summary>
    TValue? Value<TValue>(CommandOption<TValue> option);

    /// <summary>TS <c>values</c>：取全部值（未提供为空表）。</summary>
    IReadOnlyList<TValue> Values<TValue>(CommandOption<TValue> option);
}

/// <summary>对应 TS <c>CommandBuildResult&lt;TInvocation&gt;</c>。</summary>
public abstract record CommandBuildResult<TInvocation>
    where TInvocation : ICommandInvocation
{
    public sealed record Ok(TInvocation Command) : CommandBuildResult<TInvocation>;

    public sealed record Failed(IReadOnlyList<string> Errors) : CommandBuildResult<TInvocation>;
}

/// <summary>对应 TS <c>CommandParseResult</c>。</summary>
public abstract record CommandParseResult
{
    public sealed record Ok(ICommandInvocation Command) : CommandParseResult;

    public sealed record Failed(IReadOnlyList<string> Errors) : CommandParseResult;
}

/// <summary>对应 TS <c>CommandExecutionResult</c>。</summary>
public abstract record CommandExecutionResult
{
    public sealed record Ok(ICommandInvocation Command) : CommandExecutionResult;

    public sealed record Failed(IReadOnlyList<string> Errors) : CommandExecutionResult;
}

/// <summary>对应 TS <c>Command</c>。</summary>
public sealed class Command<TInvocation, TContext>
    where TInvocation : ICommandInvocation
{
    private readonly Dictionary<string, ICommandOption> _options = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegisteredCommand> _subcommands = new(StringComparer.Ordinal);
    private Func<IParsedCommandInput, CommandBuildResult<TInvocation>>? _builder;
    private Func<TInvocation, TContext, Task>? _action;

    public Command(string name) => Name = name;

    public string Name { get; }

    /// <summary>对应 TS <c>option</c>：重复注册同名选项直接抛错（TS 同样抛）。</summary>
    public Command<TInvocation, TContext> Option<TValue>(CommandOption<TValue> option)
    {
        if (!_options.TryAdd(option.Name, option))
        {
            throw new InvalidOperationException($"Option {option.Name} is already registered for {Name}");
        }

        return this;
    }

    public Command<TInvocation, TContext> Build(
        Func<IParsedCommandInput, CommandBuildResult<TInvocation>> builder)
    {
        _builder = builder;
        return this;
    }

    public Command<TInvocation, TContext> Action(Func<TInvocation, TContext, Task> action)
    {
        _action = action;
        return this;
    }

    /// <summary>
    /// 对应 TS <c>command</c>：注册子命令；重名抛错。
    /// </summary>
    /// <remarks>C# 不允许成员名与封闭类型同名（CS0542），故方法改名为 <c>RegisterCommand</c>。</remarks>
    public Command<TInvocation, TContext> RegisterCommand<TSubInvocation, TSubContext>(
        Command<TSubInvocation, TSubContext> command)
        where TSubInvocation : ICommandInvocation
    {
        if (_subcommands.ContainsKey(command.Name))
        {
            throw new InvalidOperationException($"Command {command.Name} is already registered");
        }

        _subcommands[command.Name] = new RegisteredCommand(
            argv => command.Parse(argv),
            (argv, context) => command.ExecuteAsync(argv, (TSubContext)context!));
        return this;
    }

    /// <summary>对应 TS <c>parse</c>：先试子命令，再按本命令的选项表解析。</summary>
    public CommandParseResult Parse(IReadOnlyList<string> argv)
    {
        var selected = Select(argv);
        if (selected is not null)
        {
            return selected.Command.Parse(selected.Argv);
        }

        return ParseOwn(argv);
    }

    /// <summary>对应 TS <c>execute</c>：解析成功且有 action 时执行它。</summary>
    public async Task<CommandExecutionResult> ExecuteAsync(IReadOnlyList<string> argv, TContext context)
    {
        var selected = Select(argv);
        if (selected is not null)
        {
            return await selected.Command.ExecuteAsync(selected.Argv, context!).ConfigureAwait(false);
        }

        var parsed = ParseOwn(argv);
        if (parsed is CommandParseResult.Failed failed)
        {
            return new CommandExecutionResult.Failed(failed.Errors);
        }

        var invocation = ((CommandParseResult.Ok)parsed).Command;
        if (_action is null)
        {
            throw new InvalidOperationException($"Command {Name} does not define an action");
        }

        await _action((TInvocation)invocation, context).ConfigureAwait(false);
        return new CommandExecutionResult.Ok(invocation);
    }

    private SelectedCommand? Select(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
        {
            return null;
        }

        return _subcommands.TryGetValue(argv[0], out var command)
            ? new SelectedCommand(command, argv.Skip(1).ToList())
            : null;
    }

    private CommandParseResult ParseOwn(IReadOnlyList<string> argv)
    {
        if (_builder is null)
        {
            throw new InvalidOperationException($"Command {Name} does not define a builder");
        }

        var parsed = ParseOptions(argv);
        var input = new ParsedCommandInput(parsed);
        var built = _builder(input);

        var errors = new List<string>(parsed.Errors);
        if (built is CommandBuildResult<TInvocation>.Failed failure)
        {
            errors.AddRange(failure.Errors);
        }

        if (errors.Count > 0)
        {
            return new CommandParseResult.Failed(errors);
        }

        if (built is not CommandBuildResult<TInvocation>.Ok ok)
        {
            throw new InvalidOperationException($"Command {Name} failed without an error");
        }

        return new CommandParseResult.Ok(ok.Command);
    }

    /// <summary>
    /// 对应 TS <c>parseOptions</c>。规则（逐条对齐，都是 TS 的真实行为）：
    ///   - 遇到 `--`：把**含 `--` 本身**的剩余参数整体塞进 remainingArgs 并结束；
    ///   - 遇到未注册的选项名：同样从当前位置整体塞进 remainingArgs 并结束（不报错）；
    ///   - 开关型选项带 `=value` → 报「does not take a value」，且**不**消费后续参数；
    ///   - 取值型选项：先看 `=value`，否则看下一个参数（必须以 `-` 开头才不取）；
    ///     取到的值为空串（`--x=` 或下一个参数是空串）→ 报「requires a value」，但
    ///     下一个参数**已被消费**；
    ///   - 非 repeatable 选项第二次出现 → 报「may only be specified once」，值不写入。
    /// </summary>
    private MutableParsedCommandInput ParseOptions(IReadOnlyList<string> argv)
    {
        var parsed = new MutableParsedCommandInput();
        for (var index = 0; index < argv.Count; index++)
        {
            var argument = argv[index];
            if (argument == "--")
            {
                parsed.RemainingArgs.AddRange(argv.Skip(index));
                break;
            }

            var equals = argument.IndexOf('=');
            var name = equals == -1 ? argument : argument[..equals];
            if (!_options.TryGetValue(name, out var option))
            {
                parsed.RemainingArgs.AddRange(argv.Skip(index));
                break;
            }

            string value;
            if (option.IsFlag)
            {
                if (equals != -1)
                {
                    parsed.Errors.Add($"{name} does not take a value");
                    continue;
                }

                value = "";
            }
            else
            {
                string? candidate = equals == -1 ? null : argument[(equals + 1)..];
                if (candidate is null)
                {
                    if (index + 1 < argv.Count)
                    {
                        var next = argv[index + 1];
                        if (!next.StartsWith('-'))
                        {
                            candidate = next;
                            index++;
                        }
                    }
                }

                if (string.IsNullOrEmpty(candidate))
                {
                    parsed.Errors.Add($"{name} requires a value");
                    continue;
                }

                value = candidate;
            }

            if (!parsed.Values.TryGetValue(name, out var values))
            {
                values = [];
                parsed.Values[name] = values;
            }

            if (values.Count > 0 && !option.Repeatable)
            {
                parsed.Errors.Add($"{name} may only be specified once");
                continue;
            }

            var result = option.ParseValue(value);
            if (result is CommandOptionParseResult<object?>.Failed failure)
            {
                parsed.Errors.Add(failure.Error);
                continue;
            }

            values.Add(((CommandOptionParseResult<object?>.Ok)result).Value);
        }

        return parsed;
    }

    private sealed record SelectedCommand(RegisteredCommand Command, IReadOnlyList<string> Argv);

    private sealed record RegisteredCommand(
        Func<IReadOnlyList<string>, CommandParseResult> Parse,
        Func<IReadOnlyList<string>, object, Task<CommandExecutionResult>> ExecuteAsync);

    private sealed class MutableParsedCommandInput
    {
        public Dictionary<string, List<object?>> Values { get; } = new(StringComparer.Ordinal);

        public List<string> RemainingArgs { get; } = [];

        public List<string> Errors { get; } = [];
    }

    private sealed class ParsedCommandInput(MutableParsedCommandInput parsed) : IParsedCommandInput
    {
        public IReadOnlyList<string> RemainingArgs => parsed.RemainingArgs;

        public TValue? Value<TValue>(CommandOption<TValue> option)
        {
            var values = Lookup(option);
            return values.Count > 0 ? (TValue)values[0]! : default;
        }

        public IReadOnlyList<TValue> Values<TValue>(CommandOption<TValue> option) =>
            [.. Lookup(option).Select(value => (TValue)value!)];

        private List<object?> Lookup<TValue>(CommandOption<TValue> option) =>
            parsed.Values.TryGetValue(option.Name, out var values) ? values : [];
    }
}

/// <summary>对应 TS <c>valueOption</c> / <c>stringOption</c> / <c>flagOption</c>。</summary>
public static class CommandOptions
{
    public static CommandOption<TValue> ValueOption<TValue>(
        string name,
        Func<string, CommandOptionParseResult<TValue>> parse,
        bool repeatable = false) => new()
        {
            Name = name,
            Parse = parse,
            Repeatable = repeatable,
        };

    public static CommandOption<string> StringOption(string name, bool repeatable = false) =>
        ValueOption(name, value => new CommandOptionParseResult<string>.Ok(value), repeatable);

    public static CommandOption<bool> FlagOption(string name) => new()
    {
        Name = name,
        IsFlag = true,
        Parse = _ => new CommandOptionParseResult<bool>.Ok(true),
    };
}
