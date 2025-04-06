using System;
using System.Threading;
using System.Threading.Tasks;

namespace RateLimiter.Policies
{
    // TODO: Idea here is that sometimes it may be desireable to deny rejected request,
    // while at other times it may be desireable instead of rejecting to simply queue
    // and await when policy has an allocation to grant, without immeidately being
    // rejected.  Not implemented, but retained here for the idea.
    // As well as possibly future use for other set of options.
    // public readonly struct RatePolicyOptions
    // {
    //     public bool AwaitLease { get; init; } = false;

    //     public RatePolicyOptions()
    //     {
    //     }
    // }

    /// <summary>
    /// Base class for all rate policies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the base class that all types of policies must derive from, and provides
    /// general mean of evaluating policy's grant for a rquest.
    /// </para>
    /// <para>
    /// In general, the inheritance hierarchy is expected to be <c>ConcreteKindRatePolicy : KindRatePolicy : RatePolicy</c> , where:
    /// <list type="table">
    /// <item>
    ///     <term>RatePolicy</term>
    ///     <description>Base for all policies (this class).</description>
    /// </item>
    /// <item>
    ///     <term>KindRatePolicy</term>
    ///     <description>
    ///         The kind of policy, like <see cref="FixedWindowRatePolicy">, or
    ///         <see cref="ConcurrentWindowRatePolicy"/> that are still <see langword="abstract" /> classes
    ///         that define properties of the policy and are to be inherited by state provider's specific
    ///         implementations.
    ///     </description>
    /// </item>
    /// <item>
    ///     <term>ConcreteKindRatePolicy</term>
    ///     <description>
    ///         Concrete, store-provider-specific implementation of <c>KindRatePolicy</c>.
    ///         This implementation would typically deal with actual evaluation of policy with
    ///         knowledge how to retrieve and persist policy state information with that specifc
    ///         state store mechanism.
    ///     </description>
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// While library currently provides a few Kind of policies -- namely <see cref="FixedWindowRatePolicy"/>,
    /// <see cref="SlidingWindowRatePolicy"/>, and <see cref="ConcurrentWindowRatePolicy"/>, along with
    /// <see cref="StateProviders.DefaultStateProvider"/> that implements them all -- new kinds of policies
    /// can be introduced, thereby extending provided set.  When new kind of policies are introduced, a state
    /// provider supporting those also needs to be introduced (a new one, or one extending support from
    /// existing ones).  Newly introduced, generally reusable policies are candidates to be included in
    /// the general distribution of the library.
    /// <para>
    /// </remarks>
    public abstract class RatePolicy
    {
        // See above comment.
        // protected RatePolicyOptions PolicyOptions { get; init; }

        /// <summary>
        /// Obtains lease for a specified request key.  Caller should examine
        /// <see cref="PolicyLease.IsGranted"> to see if request is granted by the policy.
        /// </summary>
        /// <param name="requestPolicyKey">Key of the request scope by which allocation bucket policy is to be evaluated.</param>
        /// <param name="cancel">Cancellation token.</param>
        /// <returns>Async; An instance of <see cref="PolicyLease">.</returns>
        public abstract Task<PolicyLease> ObtainLease(string requestPolicyKey, CancellationToken cancel = default);
    }

    /// <summary>
    /// Fixed-window policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fixed-window policy is a kind where a fixed number of
    /// requests <see cref="PeriodQuantity"/> are allowed during a <see cref="Period"/>. Requests can be
    /// granted anywhere during the period upto the allowed quantity, and then quantity is not
    /// replenished until new period begins.
    /// </para>
    /// </remarks>
    public abstract class FixedWindowRatePolicy : RatePolicy
    {
        /// <summary>
        /// Period for replenishment.
        /// </summary>
        public TimeSpan Period { get; set; }

        /// <summary>
        /// Allowed quantity per period.
        /// </summary>
        public uint PeriodQuantity { get; set; }
    }

    /// <summary>
    /// Sliding-window policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sliding-window policy is a kind where a fixed number of
    /// requests <see cref="PeriodQuantity"/> are allowed during a <see cref="Period"/>, where period "slides"
    /// with the request. In other words, at any point in time, it looks back the exact period to ensure new
    /// request doesn't exceed total allowed requests.  The moment prior request expires out of the look-back
    /// window, it's allocation is now available back in the pool.
    /// </para>
    /// </remarks>
    public abstract class SlidingWindowRatePolicy : RatePolicy
    {
        /// <summary>
        /// Sliding period for replenishment.
        /// </summary>
        public TimeSpan Period { get; set; }

        /// <summary>
        /// Allowed quantity per period.
        /// </summary>
        public uint PeriodQuantity { get; set; }
    }

    /// <summary>
    /// Concurrent-window policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Concurrent-window policy is a kind where there is a strict <see cref="Degree"/>
    /// limit on the number of concurrently executing requests. The moment request
    /// completes execution, its allocation is immediately returned to the pool to
    /// be available for use by the next request.
    /// </para>
    /// </remarks>
    public abstract class ConcurrentWindowRatePolicy : RatePolicy
    {
        /// <summary>
        /// Concurrency degree. I.e. number of allowed concurrently executing requests.
        /// </summary>
        public uint Degree { get; set; }
    }
}