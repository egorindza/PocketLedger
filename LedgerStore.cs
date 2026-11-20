using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PocketLedger;

public sealed class LedgerStore(string path)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path, ForeignKeys = true, DefaultTimeout = 8
    }.ConnectionString;

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = Open();
        using var journal = Command(connection, null, "PRAGMA journal_mode=WAL;");
        journal.ExecuteScalar();
        using var tx = connection.BeginTransaction();
        Execute(connection, tx, """
            CREATE TABLE IF NOT EXISTS Subscriptions (
                Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Category TEXT NOT NULL, Minor INTEGER NOT NULL CHECK(Minor > 0),
                Currency INTEGER NOT NULL CHECK(Currency BETWEEN 0 AND 2), Cycle INTEGER NOT NULL CHECK(Cycle BETWEEN 0 AND 3),
                Due TEXT NOT NULL, BillingDay INTEGER NOT NULL CHECK(BillingDay BETWEEN 1 AND 31),
                Paused INTEGER NOT NULL CHECK(Paused IN (0,1)), Version INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Prices (
                Id INTEGER PRIMARY KEY, SubscriptionId INTEGER NOT NULL REFERENCES Subscriptions(Id), RecordedAt TEXT NOT NULL,
                Minor INTEGER NOT NULL, Currency INTEGER NOT NULL, Cycle INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Prices_Subscription ON Prices(SubscriptionId, Id);
            CREATE TABLE IF NOT EXISTS Expenses (
                Id TEXT PRIMARY KEY, SubscriptionId INTEGER REFERENCES Subscriptions(Id), Name TEXT NOT NULL, Category TEXT NOT NULL,
                Minor INTEGER NOT NULL CHECK(Minor > 0), Currency INTEGER NOT NULL CHECK(Currency BETWEEN 0 AND 2),
                PaidOn TEXT NOT NULL, ScheduledOn TEXT, Version INTEGER NOT NULL,
                CHECK((SubscriptionId IS NULL) = (ScheduledOn IS NULL)),
                UNIQUE(SubscriptionId, ScheduledOn)
            );
            CREATE INDEX IF NOT EXISTS IX_Expenses_Date ON Expenses(PaidOn DESC);
            CREATE TABLE IF NOT EXISTS ExpenseChanges (
                Id INTEGER PRIMARY KEY, ExpenseId TEXT NOT NULL REFERENCES Expenses(Id), ChangedAt TEXT NOT NULL,
                Name TEXT NOT NULL, Category TEXT NOT NULL, Minor INTEGER NOT NULL, Currency INTEGER NOT NULL, PaidOn TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ExpenseChanges_Expense ON ExpenseChanges(ExpenseId, Id);
            """);
        tx.Commit();
    }

    public IReadOnlyList<Subscription> Subscriptions() => ReadAll(
        PlanColumns + " ORDER BY Paused, Due, Name;", Plan);

    public Subscription? Subscription(long id)
    {
        using var connection = Open();
        return ReadPlan(connection, null, id);
    }

    public IReadOnlyList<PriceVersion> Prices(long id) => ReadAll(
        "SELECT RecordedAt, Minor, Currency, Cycle FROM Prices WHERE SubscriptionId=$id ORDER BY Id DESC;",
        r => new PriceVersion(Instant(r.GetString(0)), r.GetInt64(1), (Currency)r.GetInt32(2), (Cycle)r.GetInt32(3)), ("$id", id));

    public IReadOnlyList<Expense> Expenses(DateOnly? from = null, DateOnly? through = null, Currency? currency = null, long? subscription = null) =>
        ReadAll(ExpenseColumns + """
            WHERE ($from IS NULL OR PaidOn >= $from) AND ($through IS NULL OR PaidOn <= $through)
              AND ($currency IS NULL OR Currency = $currency) AND ($subscription IS NULL OR SubscriptionId = $subscription)
            ORDER BY PaidOn DESC, rowid DESC;
            """, Entry, ("$from", Day(from)), ("$through", Day(through)), ("$currency", currency is null ? null : (int)currency),
            ("$subscription", subscription));

    public Expense? Expense(Guid id)
    {
        using var connection = Open();
        return ReadEntry(connection, null, id);
    }

    public IReadOnlyList<ExpenseVersion> Changes(Guid id) => ReadAll(
        "SELECT ChangedAt, Name, Category, Minor, Currency, PaidOn FROM ExpenseChanges WHERE ExpenseId=$id ORDER BY Id DESC;",
        r => new ExpenseVersion(Instant(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetInt64(3), (Currency)r.GetInt32(4),
            ParseDay(r.GetString(5))), ("$id", Key(id)));

    public long Save(SubscriptionForm form, DateTime now)
    {
        var money = Minor(form.Amount);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var id = form.Id;
        var current = id == 0 ? null : ReadPlan(connection, tx, id) ?? throw new LedgerException("Подписка не найдена.");
        if (current is not null && current.Version != form.Version)
            throw new LedgerException("Подписка уже изменена в другой вкладке. Откройте её заново.");

        if (current is null)
        {
            using var insert = Command(connection, tx, """
                INSERT INTO Subscriptions (Name,Category,Minor,Currency,Cycle,Due,BillingDay,Paused,Version)
                VALUES ($name,$category,$minor,$currency,$cycle,$due,$day,$paused,1) RETURNING Id;
                """, PlanParams(form, money, form.Due.Day));
            id = (long)insert.ExecuteScalar()!;
        }
        else
        {
            var parameters = PlanParams(form, money, current.Due == form.Due ? current.BillingDay : form.Due.Day);
            Execute(connection, tx, """
                UPDATE Subscriptions SET Name=$name, Category=$category, Minor=$minor, Currency=$currency, Cycle=$cycle,
                    Due=$due, BillingDay=$day, Paused=$paused, Version=Version+1 WHERE Id=$id;
                """, [.. parameters, ("$id", id)]);
        }

        if (current is null || current.Minor != money || current.Currency != form.Currency || current.Cycle != form.Cycle)
        {
            Execute(connection, tx, """
                INSERT INTO Prices (SubscriptionId,RecordedAt,Minor,Currency,Cycle) VALUES ($id,$at,$minor,$currency,$cycle);
                """, ("$id", id), ("$at", now.ToString("O")), ("$minor", money),
                ("$currency", (int)form.Currency), ("$cycle", (int)form.Cycle));
        }
        tx.Commit();
        return id;
    }

    public void MovePayment(long id, DateOnly expected, int version)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var plan = RequirePlan(connection, tx, id, expected, version);
        Advance(connection, tx, plan);
        tx.Commit();
    }

    public void Save(ExpenseForm form, DateTime now)
    {
        var money = Minor(form.Amount);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        if (form.Version == 0)
        {
            if (ReadEntry(connection, tx, form.Id) is not null) return;
            var plan = form.SubscriptionId is long id
                ? RequirePlan(connection, tx, id, form.ScheduledOn, form.SubscriptionVersion)
                : null;
            var scheduled = plan?.Due;
            if (plan is not null)
            {
                using var duplicate = Command(connection, tx, "SELECT 1 FROM Expenses WHERE SubscriptionId=$id AND ScheduledOn=$due;",
                    ("$id", plan.Id), ("$due", Day(plan.Due)));
                if (duplicate.ExecuteScalar() is not null)
                    throw new LedgerException("Этот платёж уже записан. Его сумму можно поправить в истории.");
            }
            Execute(connection, tx, """
                INSERT INTO Expenses (Id,SubscriptionId,Name,Category,Minor,Currency,PaidOn,ScheduledOn,Version)
                VALUES ($id,$subscription,$name,$category,$minor,$currency,$paid,$due,1);
                """, [.. ExpenseParams(form, money), ("$subscription", plan?.Id), ("$due", Day(scheduled))]);
            if (plan is not null) Advance(connection, tx, plan);
        }
        else
        {
            var current = ReadEntry(connection, tx, form.Id) ?? throw new LedgerException("Расход не найден.");
            if (current.Version != form.Version)
                throw new LedgerException("Эта запись уже изменена. Откройте её заново.");
            if (current.Minor == money && current.Currency == form.Currency && current.PaidOn == form.PaidOn &&
                current.Name == form.Name.Trim() && current.Category == form.Category.Trim()) return;

            Execute(connection, tx, """
                INSERT INTO ExpenseChanges (ExpenseId,ChangedAt,Name,Category,Minor,Currency,PaidOn)
                SELECT Id,$now,Name,Category,Minor,Currency,PaidOn FROM Expenses WHERE Id=$id;
                """, ("$id", Key(form.Id)), ("$now", now.ToString("O")));
            Execute(connection, tx, """
                UPDATE Expenses SET Name=$name,Category=$category,Minor=$minor,Currency=$currency,PaidOn=$paid,Version=Version+1
                WHERE Id=$id;
                """, ExpenseParams(form, money));
        }
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private List<T> ReadAll<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] values)
    {
        using var connection = Open();
        using var command = Command(connection, null, sql, values);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (parameter, value) in values) command.Parameters.AddWithValue(parameter, value ?? DBNull.Value);
        return command;
    }

    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object?)[] values)
    {
        using var command = Command(db, tx, sql, values);
        command.ExecuteNonQuery();
    }

    private const string PlanColumns = "SELECT Id, Name, Category, Minor, Currency, Cycle, Due, BillingDay, Paused, Version FROM Subscriptions ";
    private const string ExpenseColumns = "SELECT Id, SubscriptionId, Name, Category, Minor, Currency, PaidOn, ScheduledOn, Version FROM Expenses ";

    private static Subscription Plan(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
        (Currency)r.GetInt32(4), (Cycle)r.GetInt32(5), ParseDay(r.GetString(6)), r.GetInt32(7), r.GetBoolean(8), r.GetInt32(9));

    private static Expense Entry(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), r.IsDBNull(1) ? null : r.GetInt64(1),
        r.GetString(2), r.GetString(3), r.GetInt64(4), (Currency)r.GetInt32(5), ParseDay(r.GetString(6)),
        r.IsDBNull(7) ? null : ParseDay(r.GetString(7)), r.GetInt32(8));

    private static Subscription? ReadPlan(SqliteConnection connection, SqliteTransaction? tx, long id)
    {
        using var command = Command(connection, tx, PlanColumns + "WHERE Id=$id;", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Plan(reader) : null;
    }

    private static Expense? ReadEntry(SqliteConnection connection, SqliteTransaction? tx, Guid id)
    {
        using var command = Command(connection, tx, ExpenseColumns + "WHERE Id=$id;", ("$id", Key(id)));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Entry(reader) : null;
    }

    private static Subscription RequirePlan(SqliteConnection db, SqliteTransaction tx, long id, DateOnly? expected, int version)
    {
        var plan = ReadPlan(db, tx, id) ?? throw new LedgerException("Подписка не найдена.");
        if (plan.Paused) throw new LedgerException("Подписка на паузе. Возобновите её перед оплатой.");
        if (plan.Due != expected || plan.Version != version)
            throw new LedgerException("Сумма или дата уже изменились. Откройте подписку заново.");
        return plan;
    }

    private static void Advance(SqliteConnection db, SqliteTransaction tx, Subscription plan)
    {
        var next = Display.Advance(plan.Due, plan.Cycle, plan.BillingDay);
        Execute(db, tx, "UPDATE Subscriptions SET Due=$next, Version=Version+1 WHERE Id=$id;",
            ("$next", Day(next)), ("$id", plan.Id));
    }

    private static (string, object?)[] PlanParams(SubscriptionForm form, long minor, int billingDay) =>
        [("$name", form.Name.Trim()), ("$category", form.Category.Trim()), ("$minor", minor), ("$currency", (int)form.Currency),
         ("$cycle", (int)form.Cycle), ("$due", Day(form.Due)), ("$day", billingDay), ("$paused", form.Paused ? 1 : 0)];

    private static (string, object?)[] ExpenseParams(ExpenseForm form, long minor) =>
        [("$id", Key(form.Id)), ("$name", form.Name.Trim()), ("$category", form.Category.Trim()),
         ("$minor", minor), ("$currency", (int)form.Currency), ("$paid", Day(form.PaidOn))];

    private static string Key(Guid id) => id.ToString("N");
    private static long Minor(decimal amount) => (long)decimal.Round(amount * 100, 0, MidpointRounding.AwayFromZero);
    private static string? Day(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly ParseDay(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTime Instant(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
