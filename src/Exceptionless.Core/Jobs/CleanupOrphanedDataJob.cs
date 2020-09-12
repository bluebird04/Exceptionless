﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Models;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Utility;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Nest;

namespace Exceptionless.Core.Jobs {
    [Job(Description = "Deletes orphaned data.", IsContinuous = false)]
    public class CleanupOrphanedDataJob : JobWithLockBase, IHealthCheck {
        private readonly IElasticClient _elasticClient;
        private readonly ILockProvider _lockProvider;
        private DateTime? _lastRun;

        public CleanupOrphanedDataJob(IElasticClient elasticClient, ICacheClient cacheClient, ILoggerFactory loggerFactory = null) : base(loggerFactory) {
            _elasticClient = elasticClient;
            _lockProvider = new ThrottlingLockProvider(cacheClient, 1, TimeSpan.FromDays(1));
        }

        protected override Task<ILock> GetLockAsync(CancellationToken cancellationToken = default) {
            return _lockProvider.AcquireAsync(nameof(CleanupOrphanedDataJob), TimeSpan.FromHours(2), new CancellationToken(true));
        }

        protected override async Task<JobResult> RunInternalAsync(JobContext context) {
            await DeleteOrphanedEventsByStackAsync(context).AnyContext();
            await DeleteOrphanedEventsByProjectAsync(context).AnyContext();
            await DeleteOrphanedEventsByOrganizationAsync(context).AnyContext();

            return JobResult.Success;
        }

        public async Task DeleteOrphanedEventsByStackAsync(JobContext context) {
            // get approximate number of unique stack ids
            var stackCardinality = await _elasticClient.SearchAsync<PersistentEvent>(s => s.Aggregations(a => a
                .Cardinality("cardinality_stack_id", c => c.Field(f => f.StackId).PrecisionThreshold(40000))));

            var uniqueStackIdCount = stackCardinality.Aggregations.Cardinality("cardinality_stack_id").Value;
            if (!uniqueStackIdCount.HasValue || uniqueStackIdCount.Value <= 0)
                return;

            // break into batches of 500
            const int batchSize = 500;
            int buckets = (int)uniqueStackIdCount.Value / batchSize;
            buckets = Math.Max(1, buckets);

            for (int batchNumber = 0; batchNumber < buckets; batchNumber++) {
                await RenewLockAsync(context);

                var stackIdTerms = await _elasticClient.SearchAsync<PersistentEvent>(s => s.Aggregations(a => a
                    .Terms("terms_stack_id", c => c.Field(f => f.StackId).Include(batchNumber, buckets).Size(batchSize * 2))));

                var stackIds = stackIdTerms.Aggregations.Terms("terms_stack_id").Buckets.Select(b => b.Key).ToArray();
                if (stackIds.Length == 0)
                    continue;

                var stacks = await _elasticClient.MultiGetAsync(r => r.SourceEnabled(false).GetMany<Stack>(stackIds));
                var missingStackIds = stacks.Hits.Where(h => !h.Found).Select(h => h.Id).ToArray();

                if (missingStackIds.Length == 0)
                    continue;

                _logger.LogInformation("Found {OrphanedEventCount} orphaned events from missing stacks {MissingStackIds}", missingStackIds.Length, missingStackIds);
                await _elasticClient.DeleteByQueryAsync<PersistentEvent>(r => r.Query(q => q.Terms(t => t.Field(f => f.StackId).Terms(missingStackIds))));
            }
        }

        public async Task DeleteOrphanedEventsByProjectAsync(JobContext context) {
            // get approximate number of unique project ids
            var projectCardinality = await _elasticClient.SearchAsync<PersistentEvent>(s => s.Aggregations(a => a
                .Cardinality("cardinality_project_id", c => c.Field(f => f.ProjectId).PrecisionThreshold(40000))));

            var uniqueProjectIdCount = projectCardinality.Aggregations.Cardinality("cardinality_project_id").Value;
            if (!uniqueProjectIdCount.HasValue || uniqueProjectIdCount.Value <= 0)
                return;

            // break into batches of 500
            const int batchSize = 500;
            int buckets = (int)uniqueProjectIdCount.Value / batchSize;
            buckets = Math.Max(1, buckets);

            for (int batchNumber = 0; batchNumber < buckets; batchNumber++) {
                await RenewLockAsync(context);

                var projectIdTerms = await _elasticClient.SearchAsync<PersistentEvent>(s => s.Aggregations(a => a
                    .Terms("terms_project_id", c => c.Field(f => f.ProjectId).Include(batchNumber, buckets).Size(batchSize * 2))));

                var projectIds = projectIdTerms.Aggregations.Terms("terms_project_id").Buckets.Select(b => b.Key).ToArray();
                if (projectIds.Length == 0)
                    continue;

                var projects = await _elasticClient.MultiGetAsync(r => r.SourceEnabled(false).GetMany<Project>(projectIds));
                var missingProjectIds = projects.Hits.Where(h => !h.Found).Select(h => h.Id).ToArray();

                if (missingProjectIds.Length == 0)
                    continue;

                _logger.LogInformation("Found {OrphanedEventCount} orphaned events from missing projects {MissingProjectIds}", missingProjectIds.Length, missingProjectIds);
                await _elasticClient.DeleteByQueryAsync<PersistentEvent>(r => r.Query(q => q.Terms(t => t.Field(f => f.ProjectId).Terms(missingProjectIds))));
            }
        }

        public async Task DeleteOrphanedEventsByOrganizationAsync(JobContext context) {
            // get approximate number of unique organization ids
            var organizationCardinality = await _elasticClient.SearchAsync<PersistentEvent>(s => s.Aggregations(a => a
                .Cardinality("cardinality_organization_id", c => c.Field(f => f.OrganizationId).PrecisionThreshold(40000))));

            var uniqueOrganizationIdCount = organizationCardinality.Aggregations.Cardinality("cardinality_organization_id").Value;
            if (!uniqueOrganizationIdCount.HasValue || uniqueOrganizationIdCount.Value <= 0)
                return;

            // break into batches of 500
            const int batchSize = 500;
            int buckets = (int)uniqueOrganizationIdCount.Value / batchSize;
            buckets = Math.Max(1, buckets);

            for (int batchNumber = 0; batchNumber < buckets; batchNumber++) {
                await RenewLockAsync(context);

                var organizationIdTerms = await _elasticClient.SearchAsync<PersistentEvent>(s => s.Aggregations(a => a
                    .Terms("terms_organization_id", c => c.Field(f => f.OrganizationId).Include(batchNumber, buckets).Size(batchSize * 2))));

                var organizationIds = organizationIdTerms.Aggregations.Terms("terms_organization_id").Buckets.Select(b => b.Key).ToArray();
                if (organizationIds.Length == 0)
                    continue;

                var organizations = await _elasticClient.MultiGetAsync(r => r.SourceEnabled(false).GetMany<Organization>(organizationIds));
                var missingOrganizationIds = organizations.Hits.Where(h => !h.Found).Select(h => h.Id).ToArray();

                if (missingOrganizationIds.Length == 0)
                    continue;

                _logger.LogInformation("Found {OrphanedEventCount} orphaned events from missing organizations {MissingOrganizationIds}", missingOrganizationIds.Length, missingOrganizationIds);
                await _elasticClient.DeleteByQueryAsync<PersistentEvent>(r => r.Query(q => q.Terms(t => t.Field(f => f.OrganizationId).Terms(missingOrganizationIds))));
            }
        }

        private Task RenewLockAsync(JobContext context) {
            _lastRun = SystemClock.UtcNow;
            return context.RenewLockAsync();
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) {
            if (!_lastRun.HasValue)
                return Task.FromResult(HealthCheckResult.Healthy("Job has not been run yet."));

            if (SystemClock.UtcNow.Subtract(_lastRun.Value) > TimeSpan.FromMinutes(65))
                return Task.FromResult(HealthCheckResult.Unhealthy("Job has not run in the last 65 minutes."));

            return Task.FromResult(HealthCheckResult.Healthy("Job has run in the last 65 minutes."));
        }
    }
}