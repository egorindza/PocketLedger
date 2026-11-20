using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace PocketLedger;

public enum Cycle { Weekly, Monthly, Quarterly, Annual }
public enum Currency { RUB, USD, EUR }

public sealed record Subscription(long Id, string Name, string Category, long Minor, Currency Currency,
    Cycle Cycle, DateOnly Due, int BillingDay, bool Paused, int Version);

public sealed record Expense(Guid Id, long? SubscriptionId, string Name, string Category,
    long Minor, Currency Currency, DateOnly PaidOn, DateOnly? ScheduledOn, int Version);

public sealed record PriceVersion(DateTime RecordedAt, long Minor, Currency Currency, Cycle Cycle);
public sealed record ExpenseVersion(DateTime ChangedAt, string Name, string Category, long Minor, Currency Currency, DateOnly PaidOn);

public sealed class SubscriptionForm
{
    public long Id { get; set; }
    public int Version { get; set; }
    [Required, StringLength(90)] public string Name { get; set; } = "";
    [Required, StringLength(60)] public string Category { get; set; } = "";
    [Range(typeof(decimal), "0.01", "100000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true, ErrorMessage = "Укажите сумму от 0,01 до 100 000 000.")] public decimal Amount { get; set; }
    [EnumDataType(typeof(Currency))] public Currency Currency { get; set; }
    [EnumDataType(typeof(Cycle))] public Cycle Cycle { get; set; } = Cycle.Monthly;
    [DataType(DataType.Date)] public DateOnly Due { get; set; }
    public bool Paused { get; set; }

    public static SubscriptionForm From(Subscription row) => new()
    {
        Id = row.Id, Version = row.Version, Name = row.Name, Category = row.Category, Amount = row.Minor / 100m,
        Currency = row.Currency, Cycle = row.Cycle, Due = row.Due, Paused = row.Paused
    };
}

public sealed class ExpenseForm
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Version { get; set; }
    public long? SubscriptionId { get; set; }
    public int SubscriptionVersion { get; set; }
    public DateOnly? ScheduledOn { get; set; }
    [Required, StringLength(90)] public string Name { get; set; } = "";
    [Required, StringLength(60)] public string Category { get; set; } = "";
    [Range(typeof(decimal), "0.01", "100000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true, ErrorMessage = "Укажите сумму от 0,01 до 100 000 000.")] public decimal Amount { get; set; }
    [EnumDataType(typeof(Currency))] public Currency Currency { get; set; }
    [DataType(DataType.Date)] public DateOnly PaidOn { get; set; }

    public static ExpenseForm From(Expense row) => new()
    {
        Id = row.Id, Version = row.Version, SubscriptionId = row.SubscriptionId, ScheduledOn = row.ScheduledOn,
        Name = row.Name, Category = row.Category, Amount = row.Minor / 100m, Currency = row.Currency, PaidOn = row.PaidOn
    };
}

public sealed record Money(Currency Currency, long Minor);
public sealed record SpendingCategory(string Name, long Minor);
public sealed record Dashboard(DateOnly Today, IReadOnlyList<Subscription> Subscriptions, IReadOnlyList<Expense> Expenses,
    Currency ChartCurrency, bool Sample)
{
    public DateOnly Month => new(Today.Year, Today.Month, 1);
    public IReadOnlyList<Subscription> Due => Subscriptions.Where(x => !x.Paused && x.Due < Today.AddDays(30)).OrderBy(x => x.Due).ToList();
    public IReadOnlyList<Expense> MonthExpenses => Expenses.Where(x => x.PaidOn >= Month && x.PaidOn <= Today).ToList();
    public IReadOnlyList<Money> Monthly => Display.Totals(MonthExpenses.Select(x => new Money(x.Currency, x.Minor)));
    public IReadOnlyList<Money> Scheduled => Display.Totals(Due.Select(x => new Money(x.Currency, x.Minor)));
    public IReadOnlyList<SpendingCategory> Categories => MonthExpenses.Where(x => x.Currency == ChartCurrency)
        .GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase).Select(x => new SpendingCategory(x.Key, x.Sum(y => y.Minor)))
        .OrderByDescending(x => x.Minor).ThenBy(x => x.Name).ToList();
}

public sealed record SubscriptionPage(Subscription Item, IReadOnlyList<PriceVersion> Prices, IReadOnlyList<Expense> Payments);
public sealed record HistoryPage(DateOnly Today, string Period, Currency? Currency, IReadOnlyList<Expense> Expenses, int Page)
{
    public IReadOnlyList<Money> Totals => Display.Totals(Expenses.Select(x => new Money(x.Currency, x.Minor)));
    public int PageCount => Math.Max(1, (Expenses.Count + 29) / 30);
    public IEnumerable<Expense> Visible => Expenses.Skip((Page - 1) * 30).Take(30);
}

public static class Display
{
    public static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public static readonly string[] Categories = ["Развлечения", "Музыка", "Облако", "Работа", "Связь", "Дом", "Еда", "Здоровье", "Другое"];
    public static string Money(long cents, Currency currency) =>
        (cents / 100m).ToString(cents % 100 == 0 ? "N0" : "N2", Russian) + " " + (currency switch
        {
            Currency.RUB => "₽", Currency.USD => "$", _ => "€"
        });

    public static string CycleName(Cycle cycle) => cycle switch
    {
        Cycle.Weekly => "Раз в неделю", Cycle.Monthly => "Раз в месяц", Cycle.Quarterly => "Раз в квартал", _ => "Раз в год"
    };

    public static string CycleShort(Cycle cycle) => cycle switch
    {
        Cycle.Weekly => "/ нед.", Cycle.Monthly => "/ мес.", Cycle.Quarterly => "/ кв.", _ => "/ год"
    };

    public static string Date(DateOnly date) => date.ToString("d MMM", Russian);
    public static string Initial(string title) => StringInfo.GetNextTextElement(title);
    public static IReadOnlyList<Money> Totals(IEnumerable<Money> amounts) =>
        amounts.GroupBy(x => x.Currency).Select(x => new Money(x.Key, x.Sum(y => y.Minor))).OrderBy(x => x.Currency).ToList();

    public static DateOnly Advance(DateOnly due, Cycle cycle, int billingDay)
    {
        if (cycle == Cycle.Weekly) return due.AddDays(7);
        var months = cycle switch { Cycle.Monthly => 1, Cycle.Quarterly => 3, Cycle.Annual => 12, _ => throw new ArgumentOutOfRangeException(nameof(cycle)) };
        var month = new DateOnly(due.Year, due.Month, 1).AddMonths(months);
        return new(month.Year, month.Month, Math.Min(billingDay, DateTime.DaysInMonth(month.Year, month.Month)));
    }
}

public sealed class LedgerException(string message) : Exception(message);
