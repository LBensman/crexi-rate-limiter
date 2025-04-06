

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RateLimiter.Policies;

namespace RateLimiter
{
    public class PolicyLease : IAsyncDisposable
    {
        public bool IsGranted { get; private set; }

        private readonly Func<Task>? _onRelease;

        public PolicyLease(bool granted, Action onRelease)
            : this(granted, () => { onRelease?.Invoke(); return Task.CompletedTask; })
        { }

        public PolicyLease(bool granted, Func<Task> onRelease)
            : this(granted)
        {
            _onRelease = onRelease;
        }

        public PolicyLease(bool granted)
        {
            IsGranted = granted;
        }

        public virtual async ValueTask DisposeAsync()
        {
            await (_onRelease?.Invoke() ?? ValueTask.CompletedTask.AsTask());
        }
    }

    public interface IPolicyStateProvider
    {
        TPolicyType GetPolicy<TPolicyType>() where TPolicyType : RatePolicy;
    }

    public interface IRequest
    {
        // byte[] GetRequestHash();
        // IEnumerable<IRatePolicy> GetPolicies();
    }

    public interface IRequestProcessor
    {
        Task AcceptRequest(IRequest request/*, failureContext*/, CancellationToken cancel = default);

        Task DenyRequest(IRequest request/*, failureContext*/, CancellationToken cancel = default);

        Task<IEnumerable<KeyValuePair<string, RatePolicy>>> GetRequestPolicies(IRequest request, CancellationToken cancel = default);
    }

    public sealed class RateLimiter
    {
        private readonly IRequestProcessor requestProcessor;

        public RateLimiter(IRequestProcessor requestProcessor)
        {
            // todo
            this.requestProcessor = requestProcessor;
        }

        public async Task CheckRequest(IRequest request, CancellationToken cancel = default)
        {
            var requestPolicyMatches = await requestProcessor.GetRequestPolicies(request, cancel);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var cancelationToken = cts.Token;

            var rejectedPolicyDetected = false;
            var policyEvalTasks = requestPolicyMatches.Select(async kv => {
                var (requestPolicyKey, policy) = kv;
                var policyLease = await policy.ObtainLease(requestPolicyKey, cancelationToken);
                if (!policyLease.IsGranted)
                {
                    cts.Cancel();
                    rejectedPolicyDetected = true;
                }
                return policyLease;
            }).ToArray();

            await Task.WhenAll(policyEvalTasks);

            // TODO: what to do in case policy eval threw exception.  Does it mean reject request?
            var policyEvaluations = policyEvalTasks.Select(t => t.Result).ToArray();

            try
            {
                if (rejectedPolicyDetected)
                {
                    await requestProcessor.DenyRequest(request, cancel);
                }
                else
                {
                    await requestProcessor.AcceptRequest(request, cancel);
                }
            }
            finally
            {
                await Task.WhenAll(policyEvaluations.Select(p => p.DisposeAsync().AsTask()));
            }
        }
    }
}