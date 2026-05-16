using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Jobnet.Models;

namespace Jobnet.ViewModels;

public partial class JobViewModel : ObservableObject
{
    public Job Job { get; }
    public string CompanyName { get; }
    public int CompositeScore { get; }

    public JobViewModel(Job job, string companyName, int compositeScore)
    {
        Job = job;
        CompanyName = companyName;
        CompositeScore = compositeScore;
    }

    public string Title => Job.Title;
    public InterestLevel InterestLevel => Job.InterestLevel;
    public bool IsActive => Job.IsActive;

    public string InterestGlyph => InterestLevel switch
    {
        InterestLevel.Interesting    => "★",
        InterestLevel.NotInteresting => "✗",
        _                            => " "
    };

    public string MetaLine
    {
        get
        {
            var remote = Capitalize(Job.RemoteType ?? "unknown");
            var emp    = Capitalize(Job.EmploymentType ?? "unknown");
            var age    = FormatAge(Job.DateFirstSeen);
            var status = Job.IsActive ? "" : $" · Removed {FormatAge(Job.DateRemoved ?? DateTime.UtcNow)} ago";
            var salary = FormatSalary();
            var salaryPart = string.IsNullOrEmpty(salary) ? "" : $" · {salary}";
            return $"{remote} · {emp} · Score {CompositeScore} · {age} old{salaryPart}{status}";
        }
    }

    public string SalaryDisplay => FormatSalary();

    private string FormatSalary()
    {
        if (Job.SalaryMin is null && Job.SalaryMax is null) return "";
        var cur = Job.SalaryCurrency ?? "";
        var min = Job.SalaryMin;
        var max = Job.SalaryMax;
        string range;
        if (min.HasValue && max.HasValue && min == max) range = Money(min.Value);
        else if (min.HasValue && max.HasValue)          range = $"{Money(min.Value)}–{Money(max.Value)}";
        else if (min.HasValue)                          range = $"{Money(min.Value)}+";
        else                                            range = $"≤{Money(max!.Value)}";
        var period = Job.SalaryPeriod switch { "hour" => "/hr", "month" => "/mo", "year" => "/yr", _ => "" };
        return string.IsNullOrEmpty(cur) ? $"{range}{period}" : $"{cur} {range}{period}";
    }

    private static string Money(int v)
    {
        if (v >= 1000) return $"${v / 1000}K";
        return $"${v}";
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static string FormatAge(DateTime when)
    {
        var days = (int)Math.Floor((DateTime.UtcNow - when).TotalDays);
        return days switch
        {
            < 1   => "today",
            1     => "1 day",
            < 30  => $"{days} days",
            < 60  => "1 month",
            < 365 => $"{days / 30} months",
            _     => $"{days / 365} year{(days / 365 == 1 ? "" : "s")}"
        };
    }
}
