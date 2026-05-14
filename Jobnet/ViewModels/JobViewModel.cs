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
            return $"{remote} · {emp} · Score {CompositeScore} · {age} old{status}";
        }
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
