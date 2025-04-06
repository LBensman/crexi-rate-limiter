using System;
using System.Threading;
using System.Threading.Tasks;

namespace RateLimiter.Policies
{
    public readonly struct RatePolicyOptions
    {
        public bool AwaitLease { get; init; } = false;

        public RatePolicyOptions()
        {
        }
    }

    public abstract class RatePolicy
    {
        // protected RatePolicyOptions PolicyOptions { get; init; }

        public abstract Task<PolicyLease> ObtainLease(string requestPolicyKey, CancellationToken cancel = default);
    }

    public abstract class FixedWindowRatePolicy : RatePolicy
    {
        public TimeSpan Period { get; set; }

        public uint PeriodQuantity { get; set; }
    }

    public abstract class SlidingWindowRatePolicy : RatePolicy
    {
        public TimeSpan Period { get; set; }

        public uint PeriodQuantity { get; set; }
    }

    public abstract class ConcurrentWindowRatePolicy : RatePolicy
    {
        public uint Degree { get; set; }
    }
}