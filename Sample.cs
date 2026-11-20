namespace PocketLedger;

public static class Sample
{
    public static (IReadOnlyList<Subscription> Plans, IReadOnlyList<Expense> Entries) Create(DateOnly today)
    {
        Subscription[] plans =
        [
            Plan(1, "Кинопоиск", "Развлечения", 399, today.AddDays(2)),
            Plan(2, "Яндекс Плюс", "Музыка", 449, today.AddDays(5)),
            Plan(3, "iCloud+", "Облако", 149, today.AddDays(8)),
            Plan(4, "Figma", "Работа", 15, today.AddDays(12), Currency.USD),
            Plan(5, "Домашний интернет", "Связь", 720, today.AddDays(18)),
            Plan(6, "Google One", "Облако", 1190, today.AddDays(40), cycle: Cycle.Annual)
        ];
        var first = new DateOnly(today.Year, today.Month, 1);
        DateOnly Day(int offset) => today.AddDays(-offset) < first ? first : today.AddDays(-offset);
        Expense[] entries =
        [
            Entry("Продукты на неделю", "Еда", 2890, Day(1)),
            Entry("Кофе и обед", "Еда", 860, Day(3)),
            Entry("Кинопоиск", "Развлечения", 399, Day(4), plans[0]),
            Entry("Домашний интернет", "Связь", 720, Day(7), plans[4]),
            Entry("Настольная лампа", "Дом", 2190, Day(10)),
            Entry("Figma", "Работа", 15, Day(11), plans[3], Currency.USD),
            Entry("iCloud+", "Облако", 149, Day(13), plans[2]),
            Entry("Мобильная связь", "Связь", 550, Day(14))
        ];
        return (plans, entries);
    }

    private static Subscription Plan(long id, string name, string category, long units, DateOnly due,
        Currency currency = Currency.RUB, Cycle cycle = Cycle.Monthly) =>
        new(id, name, category, units * 100, currency, cycle, due, due.Day, false, 1);

    private static Expense Entry(string name, string category, long units, DateOnly paid, Subscription? plan = null,
        Currency currency = Currency.RUB) =>
        new(Guid.Empty, plan?.Id, name, category, units * 100, currency, paid, plan is null ? null : paid, 1);
}
