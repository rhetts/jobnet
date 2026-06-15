using System;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Services.Profiling;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Services.Workers;

/// <summary>Pulls 'company_profile' queue rows and calls
/// <see cref="ICompanyProfiler.GenerateAndPersistAsync"/> once per row. No batching —
/// each profile fetches the company's website + summarises via AI, and the prompts are
/// per-company-specific. Default cadence (60s poll, batch of 3) is more relaxed than the
/// summary worker because (a) new companies arrive sparsely and (b) per-call cost is higher
/// (HTTP fetch + AI call).</summary>
public sealed class CompanyProfileWorker : QueueWorker
{
    public CompanyProfileWorker(IServiceScopeFactory scopes) : base(scopes) { }

    protected override string WorkerName => "company_profile";
    protected override string TaskType => JobProcessingTaskTypes.CompanyProfile;
    protected override int DefaultPollSeconds => 60;
    protected override int DefaultBatchSize => 3;

    protected override async Task<bool> ProcessOneAsync(IServiceProvider scoped, DequeuedItem item, CancellationToken ct)
    {
        var companies = scoped.GetRequiredService<ICompanyRepository>();
        var company = companies.GetById(item.EntityId);
        if (company is null)
        {
            // Company deleted between enqueue and dequeue — fail the row so it stops cycling.
            throw new InvalidOperationException($"Company {item.EntityId} no longer exists.");
        }

        var profiler = scoped.GetRequiredService<ICompanyProfiler>();
        var result = await profiler.GenerateAndPersistAsync(company, ct);
        if (result.Success) return true;
        throw new InvalidOperationException(result.Error ?? "Profile generation failed without error message.");
    }

    protected override void NotifyAfterSuccess(IServiceProvider scoped, DequeuedItem item)
    {
        scoped.GetService<IEntityChangeNotifier>()?
              .Notify(EntityChangeKinds.Company, item.EntityId, EntityChangeKinds.Profile);
    }
}
