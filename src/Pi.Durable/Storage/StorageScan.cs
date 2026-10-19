using System.Text.Json;
using Pi.Durable.Types;

namespace Pi.Durable.Storage;

internal readonly record struct ScanStart(ScanOrder Order, long? After);

internal static class StorageScan
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;

    public static ScanStart Start(
        ScanOrder? requested,
        IReadOnlyDictionary<string, object?>? cursor,
        ScanOrder fallback)
    {
        if (requested is { } requestedOrder && !IsValid(requestedOrder))
            throw new ArgumentException($"Invalid scan order: {requestedOrder}", nameof(requested));
        if (cursor is null) return new ScanStart(requested ?? fallback, null);

        var after = CursorAfter(cursor);
        ScanOrder? storedOrder = null;
        if (cursor.TryGetValue("order", out var rawOrder))
        {
            storedOrder = rawOrder switch
            {
                ScanOrder enumOrder when IsValid(enumOrder) => enumOrder,
                string value when TryParseOrder(value, out var stringOrder) => stringOrder,
                JsonElement element when element.ValueKind == JsonValueKind.String
                    && TryParseOrder(element.GetString(), out var elementOrder) => elementOrder,
                _ => throw new InvalidOperationException("Invalid storage cursor"),
            };
        }

        var resolvedOrder = storedOrder ?? fallback;
        if (requested is { } queryOrder && queryOrder != resolvedOrder)
        {
            throw new InvalidOperationException(
                $"The cursor continues a {ToWireName(resolvedOrder)} scan; the query asks for {ToWireName(queryOrder)}");
        }
        return new ScanStart(resolvedOrder, after);
    }

    public static long? CursorAfter(IReadOnlyDictionary<string, object?>? cursor)
    {
        if (cursor is null) return null;
        if (!cursor.TryGetValue("after", out var value) || value is null)
            throw new InvalidOperationException("Invalid storage cursor");
        return value switch
        {
            int number => SafeInteger(number),
            long number => SafeInteger(number),
            uint number => SafeInteger(number),
            ulong number when number <= MaximumSafeInteger => (long)number,
            double number => SafeInteger(number),
            float number => SafeInteger(number),
            decimal number when number == decimal.Truncate(number)
                && number >= -MaximumSafeInteger && number <= MaximumSafeInteger => (long)number,
            JsonElement element when element.ValueKind == JsonValueKind.Number
                && element.TryGetDouble(out var number) => SafeInteger(number),
            _ => throw new InvalidOperationException("Invalid storage cursor"),
        };
    }

    public static Page<T> CreatePage<T>(IReadOnlyList<T> values, int limit, ScanOrder order = ScanOrder.Ascending)
    {
        var items = values.Take(limit).ToList();
        if (values.Count <= limit) return new Page<T> { Items = items };
        var lastId = LastIdOf(items[^1]);
        return new Page<T>
        {
            Items = items,
            Next = new Dictionary<string, object?>
            {
                ["after"] = (double)lastId,
                ["order"] = ToWireName(order),
            },
        };
    }

    public static string ToWireName(ScanOrder order) => order switch
    {
        ScanOrder.Ascending => "ascending",
        ScanOrder.Descending => "descending",
        _ => throw new ArgumentOutOfRangeException(nameof(order)),
    };

    private static bool IsValid(ScanOrder order) => order is ScanOrder.Ascending or ScanOrder.Descending;

    private static bool TryParseOrder(string? value, out ScanOrder order)
    {
        switch (value)
        {
            case "ascending":
                order = ScanOrder.Ascending;
                return true;
            case "descending":
                order = ScanOrder.Descending;
                return true;
            default:
                order = default;
                return false;
        }
    }

    private static long SafeInteger(long number)
        => number >= -MaximumSafeInteger && number <= MaximumSafeInteger
            ? number
            : throw new InvalidOperationException("Invalid storage cursor");

    private static long SafeInteger(double number)
        => double.IsFinite(number)
            && number == Math.Truncate(number)
            && number >= -MaximumSafeInteger
            && number <= MaximumSafeInteger
                ? (long)number
                : throw new InvalidOperationException("Invalid storage cursor");

    private static long LastIdOf<T>(T value) => value switch
    {
        ConversationRecord conversation => conversation.Id.Value,
        EntryRecord entry => entry.Id.Value,
        TaskRecord task => task.Id.Value,
        SubmissionRecord submission => submission.Id.Value,
        DocumentRecord document => document.Id.Value,
        _ => throw new InvalidOperationException($"Unsupported page item type {typeof(T).Name}"),
    };
}
