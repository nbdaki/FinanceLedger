using FinanceLedger.Core.Data;
using FinanceLedger.Core.Models;
using Microsoft.Data.Sqlite;

namespace FinanceLedger.Core.Services;

public sealed class LoanService
{
    private readonly LedgerService _ledger;
    private readonly LedgerDatabase _db;

    public LoanService(LedgerService ledger)
    {
        _ledger = ledger;
        _db = ledger.Database;
    }

    public List<LoanPayment> GetPayments(string loanId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,loan_id,period,due_date,payment,principal,interest,remaining_principal,status,paid_at
            FROM loan_payments WHERE loan_id=$id ORDER BY period
            """;
        cmd.Parameters.AddWithValue("$id", loanId);
        var list = new List<LoanPayment>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new LoanPayment
            {
                Id = reader.GetString(0),
                LoanId = reader.GetString(1),
                Period = reader.GetInt32(2),
                DueDate = DateTime.Parse(reader.GetString(3)),
                Payment = reader.GetDouble(4).ToDecimal(),
                Principal = reader.GetDouble(5).ToDecimal(),
                Interest = reader.GetDouble(6).ToDecimal(),
                RemainingPrincipal = reader.GetDouble(7).ToDecimal(),
                Status = reader.GetString(8),
                PaidAt = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9))
            });
        return list;
    }

    public List<LoanPayment> GetUpcoming(int days = 60)
    {
        var result = new List<LoanPayment>();
        var today = DateTime.Today;
        foreach (var loan in _ledger.GetLoans())
            result.AddRange(GetPayments(loan.Id).Where(p =>
                p.Status == "Planned" && p.DueDate >= today && p.DueDate <= today.AddDays(days)));
        return result.OrderBy(p => p.DueDate).ToList();
    }

    public List<LoanPayment> GenerateSchedule(string loanId)
    {
        var loan = _ledger.GetLoans().First(l => l.Id == loanId);
        var existing = GetPayments(loanId);
        foreach (var payment in existing.Where(p => p.Status == "Planned"))
            DeletePayment(payment.Id);

        var schedule = BuildSchedule(loan, loan.RemainingPrincipal, loan.TermMonths, loan.MonthlyPayment);
        foreach (var payment in schedule)
            InsertPayment(payment);
        _ledger.Save();
        return schedule;
    }

    private List<LoanPayment> BuildSchedule(Loan loan, decimal principal, int term, decimal fixedPayment)
    {
        var result = new List<LoanPayment>();
        var remaining = principal;
        var rate = loan.AnnualRate / 12.0;
        var months = term > 0 ? term : 360;
        if (months > 600)
            months = 600;

        var firstDue = new DateTime(DateTime.Today.Year, DateTime.Today.Month,
            Math.Min(loan.PaymentDay > 0 ? loan.PaymentDay : 10,
                     DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month)));
        if (firstDue < DateTime.Today)
            firstDue = firstDue.AddMonths(1);

        decimal standardPayment;
        if (rate <= 0)
            standardPayment = principal / months;
        else
        {
            var factor = Math.Pow(1 + rate, months);
            standardPayment = principal * (decimal)rate * (decimal)factor / ((decimal)factor - 1);
        }

        for (int i = 1; i <= months && remaining > 0.01m; i++)
        {
            var interest = remaining * (decimal)rate;
            decimal payment;
            decimal principalPart;
            switch (loan.RepaymentMethod)
            {
                case "EqualPrincipal":
                    principalPart = principal / months;
                    if (principalPart > remaining) principalPart = remaining;
                    payment = principalPart + interest;
                    break;
                case "Custom":
                    payment = fixedPayment > 0 ? fixedPayment : standardPayment;
                    principalPart = payment - interest;
                    if (principalPart <= 0) break;
                    if (principalPart > remaining) principalPart = remaining;
                    break;
                default:
                    payment = standardPayment;
                    principalPart = payment - interest;
                    if (principalPart > remaining) principalPart = remaining;
                    break;
            }
            remaining -= principalPart;
            result.Add(new LoanPayment
            {
                LoanId = loan.Id,
                Period = i,
                DueDate = firstDue.AddMonths(i - 1),
                Payment = payment,
                Principal = principalPart,
                Interest = interest,
                RemainingPrincipal = remaining
            });
        }
        return result;
    }

    public string Prepay(string loanId, decimal amount, DateTime date, bool reduceTerm, string? fundingAccountId)
    {
        var loan = _ledger.GetLoans().First(l => l.Id == loanId);
        if (amount <= 0)
            throw new ArgumentException("提前还款金额必须大于零。");
        if (amount > loan.RemainingPrincipal)
            throw new InvalidOperationException("提前还款金额不能超过剩余本金。");

        var newPrincipal = loan.RemainingPrincipal - amount;
        decimal monthly = loan.MonthlyPayment;
        int term = loan.TermMonths;
        var rate = loan.AnnualRate / 12.0;

        if (monthly <= 0 && rate > 0)
        {
            var factor = Math.Pow(1 + rate, Math.Max(term, 1));
            monthly = loan.RemainingPrincipal * (decimal)rate * (decimal)factor / ((decimal)factor - 1);
        }
        if (monthly <= 0)
            monthly = loan.RemainingPrincipal / Math.Max(term, 1);

        if (reduceTerm && rate > 0 && monthly > newPrincipal * (decimal)rate)
        {
            var n = -Math.Log(1 - (double)(newPrincipal * (decimal)rate) / (double)monthly) / Math.Log(1 + rate);
            term = Math.Max(1, (int)Math.Ceiling(n));
        }
        else if (!reduceTerm && term > 0 && rate > 0)
        {
            var factor = Math.Pow(1 + rate, term);
            monthly = newPrincipal * (decimal)rate * (decimal)factor / ((decimal)factor - 1);
        }

        _ledger.UpdateLoanPlan(loanId, newPrincipal, monthly, term);
        GenerateSchedule(loanId);

        if (!string.IsNullOrEmpty(fundingAccountId))
        {
            _ledger.CreateTransaction(new CreateTransactionRequest
            {
                OccurredAt = date,
                Type = TransactionType.Repayment,
                Channel = PaymentChannel.BankCard,
                FundingAccountId = fundingAccountId,
                Amount = amount,
                Merchant = $"提前还款：{loan.Name}",
                Note = reduceTerm ? "提前还款·缩短期限" : "提前还款·减少月供"
            });
        }

        return $"剩余本金 {newPrincipal:N2}，{(reduceTerm ? $"预计剩余 {term} 期" : $"新月供 {monthly:N2}")}";
    }

    public void MarkPaid(string paymentId)
    {
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE loan_payments SET status='Paid', paid_at=$at WHERE id=$id";
            cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$id", paymentId);
            cmd.ExecuteNonQuery();
        }
        _ledger.Save();
    }

    private void InsertPayment(LoanPayment payment)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO loan_payments(id,loan_id,period,due_date,payment,principal,interest,remaining_principal,status)
            VALUES($id,$loan,$period,$due,$payment,$principal,$interest,$remaining,'Planned')
            """;
        cmd.Parameters.AddWithValue("$id", payment.Id);
        cmd.Parameters.AddWithValue("$loan", payment.LoanId);
        cmd.Parameters.AddWithValue("$period", payment.Period);
        cmd.Parameters.AddWithValue("$due", payment.DueDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$payment", (double)payment.Payment);
        cmd.Parameters.AddWithValue("$principal", (double)payment.Principal);
        cmd.Parameters.AddWithValue("$interest", (double)payment.Interest);
        cmd.Parameters.AddWithValue("$remaining", (double)payment.RemainingPrincipal);
        cmd.ExecuteNonQuery();
    }

    private void DeletePayment(string id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM loan_payments WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
