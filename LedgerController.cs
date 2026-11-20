using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace PocketLedger;

public sealed class LedgerController(LedgerStore store, TimeProvider time) : Controller
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    [HttpGet("/")]
    [HttpGet("/demo")]
    public IActionResult Index(Currency? currency)
    {
        var sample = Request.Path == "/demo";
        var monthlyStart = new DateOnly(Today.Year, Today.Month, 1);
        var (plans, entries) = sample ? Sample.Create(Today) : (store.Subscriptions(), store.Expenses(from: monthlyStart, through: Today));
        var available = entries.Select(x => x.Currency).Distinct().Order().ToArray();
        var chartCurrency = currency is { } pick && Enum.IsDefined(pick) ? pick : available.FirstOrDefault();
        return View("Index", new Dashboard(Today, plans, entries, chartCurrency, sample));
    }

    [HttpGet("/subscriptions")]
    public IActionResult Subscriptions() => View(store.Subscriptions());

    [HttpGet("/subscriptions/{id:long}")]
    public IActionResult Subscription(long id) => store.Subscription(id) is { } item
        ? View(new SubscriptionPage(item, store.Prices(id), store.Expenses(subscription: id)))
        : NotFound();

    [HttpGet("/subscriptions/new")]
    public IActionResult NewSubscription() => View("SubscriptionForm", new SubscriptionForm { Due = Today.AddDays(7) });

    [HttpGet("/subscriptions/{id:long}/edit")]
    public IActionResult EditSubscription(long id) => store.Subscription(id) is { } item
        ? View("SubscriptionForm", SubscriptionForm.From(item))
        : NotFound();

    [HttpPost("/subscriptions/save")]
    public IActionResult SaveSubscription(SubscriptionForm form)
    {
        if (!SupportedDate(form.Due))
            ModelState.AddModelError(nameof(form.Due), "Укажите дату между 2000 и 2100 годами.");
        VerifyAmount(form.Amount);
        if (!ModelState.IsValid) return View("SubscriptionForm", form);
        try
        {
            var id = store.Save(form, Now);
            TempData["Notice"] = form.Id == 0 ? "Подписка добавлена." : "Изменения сохранены.";
            return RedirectToAction(nameof(Subscription), new { id });
        }
        catch (LedgerException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View("SubscriptionForm", form);
        }
    }

    [HttpPost("/subscriptions/{id:long}/skip")]
    public IActionResult Skip(long id, DateOnly due, int version)
    {
        try
        {
            store.MovePayment(id, due, version);
            TempData["Notice"] = "Дата перенесена на следующий цикл.";
        }
        catch (LedgerException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Subscription), new { id });
    }

    [HttpGet("/subscriptions/{subscriptionId:long}/pay")]
    public IActionResult Pay(long subscriptionId)
    {
        var item = store.Subscription(subscriptionId);
        if (item is null) return NotFound();
        if (item.Paused) return RedirectToAction(nameof(Subscription), new { id = subscriptionId });
        return View("ExpenseForm", new ExpenseForm
        {
            SubscriptionId = item.Id, SubscriptionVersion = item.Version, ScheduledOn = item.Due, PaidOn = Today,
            Name = item.Name, Category = item.Category, Amount = item.Minor / 100m, Currency = item.Currency
        });
    }

    [HttpGet("/expenses/new")]
    public IActionResult NewExpense() => View("ExpenseForm", new ExpenseForm { PaidOn = Today });

    [HttpGet("/expenses/{id:guid}/edit")]
    public IActionResult EditExpense(Guid id)
    {
        var expense = store.Expense(id);
        if (expense is null) return NotFound();
        ViewBag.Changes = store.Changes(id);
        return View("ExpenseForm", ExpenseForm.From(expense));
    }

    [HttpPost("/expenses/save")]
    public IActionResult SaveExpense(ExpenseForm form)
    {
        if (form.Id == Guid.Empty) ModelState.AddModelError("", "Откройте форму заново.");
        if (!SupportedDate(form.PaidOn) || form.PaidOn > Today)
            ModelState.AddModelError(nameof(form.PaidOn), "Дата фактического расхода должна быть сегодняшней или более ранней.");
        VerifyAmount(form.Amount);

        if (!ModelState.IsValid) return ExpenseError(form);
        try
        {
            store.Save(form, Now);
            TempData["Notice"] = form.Version == 0 ? "Расход записан." : "Расход обновлён.";
            return form.SubscriptionId is { } id && form.Version == 0
                ? RedirectToAction(nameof(Subscription), new { id })
                : RedirectToAction(nameof(History), new { period = "all" });
        }
        catch (LedgerException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return ExpenseError(form);
        }
    }

    [HttpGet("/history")]
    public IActionResult History(string? period, Currency? currency, int page = 1)
    {
        var result = Filter(period, currency);
        return View(result with { Page = Math.Clamp(page, 1, result.PageCount) });
    }

    [HttpGet("/history/export")]
    public IActionResult Export(string? period, Currency? currency)
    {
        var csv = new StringBuilder("Дата;Описание;Категория;Сумма;Валюта;Тип\r\n");
        foreach (var row in Filter(period, currency).Expenses)
        {
            csv.Append(row.PaidOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(';')
                .Append(CsvCell(row.Name)).Append(';').Append(CsvCell(row.Category)).Append(';')
                .Append((row.Minor / 100m).ToString("0.00", CultureInfo.InvariantCulture)).Append(';').Append(row.Currency)
                .Append(';').Append(row.SubscriptionId is null ? "Разовый расход" : "Подписка").Append("\r\n");
        }
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(),
            "text/csv; charset=utf-8", "pocketledger-expenses.csv");
    }

    [Route("/error")]
    [IgnoreAntiforgeryToken]
    public IActionResult Error()
    {
        Response.StatusCode = 500;
        return View();
    }

    private HistoryPage Filter(string? period, Currency? currency)
    {
        var filter = period is "year" or "all" ? period : "month";
        var selectedCurrency = currency is { } value && Enum.IsDefined(value) ? currency : null;
        DateOnly? from = filter switch
        {
            "month" => new DateOnly(Today.Year, Today.Month, 1),
            "year" => new DateOnly(Today.Year, 1, 1),
            _ => null
        };
        return new(Today, filter, selectedCurrency, store.Expenses(from, Today, selectedCurrency), 1);
    }

    private IActionResult ExpenseError(ExpenseForm form)
    {
        if (form.Version != 0) ViewBag.Changes = store.Changes(form.Id);
        return View("ExpenseForm", form);
    }

    private void VerifyAmount(decimal amount)
    {
        if (decimal.Round(amount, 2) != amount)
            ModelState.AddModelError("Amount", "Проверьте сумму: укажите до двух знаков после запятой.");
    }

    private static bool SupportedDate(DateOnly date) => date.Year is >= 2000 and <= 2100;

    private static string CsvCell(string value)
    {
        var text = value.Trim();
        if (text.Length > 0 && (text[0] is '=' or '+' or '-' or '@' || char.IsControl(text[0]))) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
