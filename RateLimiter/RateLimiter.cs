using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RateLimiter.Policies;

namespace RateLimiter
{
    /// <summary>
    /// Marker interface to represent a request for <see cref="RateLimiter"/> to examine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At this time, the interface is nothing more than a marker interface with no methods to simply aid argument typing.
    /// </para>
    /// </remarks>
    public interface IRequest
    { }

    /// <summary>
    /// Interface to represent policy factory provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Different policy state providers can have their specific implementations of general policies.
    /// This interface provides means for querying for and creating state provider specific
    /// implementations of requested policy type.
    /// </para>
    /// <para>
    /// Library includes <see cref="Policies.StateProviders.DefaultStateProvider"/>, a local memory based state provider, that's limited to local use.
    /// </para>
    /// <para>
    /// It's possible, however, to extend other providers, such as, for example, one that
    /// uses SQLServer to allow limiting requests across a farm of servers.  Or possibly one
    /// that uses Memchached as its store.  Or Reddis.  Depending on capabilities of the backing
    /// technology, it's possible that some rate policies may or may not be supported, or possibly
    /// supported, but with some affects on their precision.  For example, while it may be possible
    /// to increment a count precisely with SQLServer as SQLServer provides capabilities for atomic
    /// data modifications, the same may not be possible with Memcached if memcached doesn't support
    /// atomic, exclusive modification with guaranteed value outcome (but at the same time win on
    /// efficiency and speed of vis-a-vis SQLServer). As such, one must carefuly weigh pros and cons
    /// of each state provider before choosing one.
    /// </para>
    /// </remarks>
    public interface IPolicyStateProvider
    {
        /// <summary>
        /// Queries for and returns provider-specific implementation of requested policy <typeparamref name="TPolicyType"/>.
        /// </summary>
        /// <typeparam name="TPolicyType">Policy type implementation to create.</typeparam>
        /// <returns>Instance of specific implementation of requested policy.</returns>
        TPolicyType GetPolicy<TPolicyType>() where TPolicyType : RatePolicy;
    }

    /// <summary>
    /// Interface that generalizes representation of service-specific integration implementations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request processor is a specific implementation of some service, like ASP.NET, HttpClient, gRPC, UberRpcService, etc.
    /// Processor handles all aspects of:
    /// <list type="bullet">
    /// <item>
    ///     Integration into service context and injection of its request handling in service's request handling pipeline.
    /// </item>
    /// <item>
    ///     Passing allowed requests to service and handling of rejected requests in service meaningful way.
    /// </item>
    /// <item>
    ///     Mapping of service's way of marking of applicable policies to requests and provided
    ///     services, e.g. API endpoints.<br />
    ///     For example, one specific implementation may use attribute decorations on handling methods to specify
    ///     endpoints' applicable policies. Yet another may use attributes to decorate message payload types or
    ///     possibly message payload fields to describe applicable policies.  Yet another way may be to have
    ///     a registry generated on startup that provides mapping lookups at runtime for applicable policies.
    ///     It all depends on the what is meaningful to the underlying services for which provider is written
    ///     and how implementers chose to handle the policy references.
    /// </item>
    /// </list>
    /// </para>
    /// </remarks>
    public interface IRequestProcessor
    {
        /// <summary>
        /// Callback from <see cref="RateLimiter"/> for requests that have been determined not to be restricted by some policy.
        /// </summary>
        /// <param name="request">Requested in context of evaluation.</param>
        /// <param name="cancel">Cancellation token.</param>
        /// <returns>Async task.</returns>
        Task AcceptRequest(IRequest request/*, failureContext*/, CancellationToken cancel = default);

        /// <summary>
        /// Callback from <see cref="RateLimiter"/> for requests that have been determined to be restricted by some policy.
        /// </summary>
        /// <param name="request">Requested in context of evaluation.</param>
        /// <param name="cancel">Cancellation token.</param>
        /// <returns>Async task.</returns>
        Task DenyRequest(IRequest request/*, failureContext*/, CancellationToken cancel = default);

        /// <summary>
        /// Queries policy mapping for applicable policies for a request, and scope of application for each matched policy.
        /// </summary>
        /// <param name="request">Request to be mapped for applicable policies and their effective scopes.</param>
        /// <param name="cancel">Cancellation token.</param>
        /// <remarks>
        /// <para>
        /// Each request can have zero or more policies that are applicable to it, and service-specific processor
        /// is responsible for providing mapping between specific requests and all rate limiting policies
        /// applicable to that request, as well as the scope of each policy. Scope may be applicable either
        /// in case there's some hierarchy to request's structure or some context such as being limited
        /// per particular user, or being limited per particular argument to request.
        /// </para>
        /// <para>
        /// The return of the call is an <see cref="IEnumerable{KeyValuePair{string, RatePolicy}}">
        /// where <see cref="KeyValuePair{string, RatePolicy}.Key"/> represents some scope bucket for the policy,
        /// and <see cref="KeyValuePair{string, RatePolicy}.Value"/> is the <see cref="RatePolicy"/> applicable
        /// at that scope.
        /// </para>
        /// </remarks>
        /// <returns>
        /// Async; <see cref="IEnumerable{KeyValuePair{string, RatePolicy}}"> representing applicable rate policies
        /// and their respective scopes. See remarks.
        /// </returns>
        Task<IEnumerable<KeyValuePair<string, RatePolicy>>> GetRequestPolicies(IRequest request, CancellationToken cancel = default);
    }

    /// <summary>
    /// Rate limiter to evaluate requests for applicable rate policies and make determination
    /// if request is to be accepted or denied.
    /// </summary>
    public sealed class RateLimiter
    {
        private readonly IRequestProcessor requestProcessor;

        /// <summary>
        /// Constructs <see cref="RateLimiter"/> using service-specific implementation of <see cref="IRequestProcessor"/>.
        /// </summary>
        /// <param name="requestProcessor">Request processor.</param>
        public RateLimiter(IRequestProcessor requestProcessor)
        {
            // todo
            this.requestProcessor = requestProcessor;
        }

        /// <summary>
        /// Checks request against applicable rate policies to determine if request should be accepted or denied.
        /// </summary>
        /// <param name="request">Request to evaluate.</param>
        /// <param name="cancel">Cancellation token.</param>
        /// <remarks>
        /// <para>
        /// The method queries request processor for applicable policies, evaluates each policy,
        /// and determines if policy is to be accepted or denied.  In case of accepted requests,
        /// calls <see cref="IRequestProcessor.AcceptRequest(IRequest, CancellationToken)"/> to
        /// have processor pass request to the underlying service; otherwise calls
        /// <see cref="IRequestProcessor.DenyRequest(IRequest, CancellationToken)"/>.
        /// </para>
        /// </remarks>
        /// <returns>Async task.</returns>
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