using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RateLimiter.Policies;
using UberRpcService;

namespace RateLimiter.UberRpc;

/// <summary>
/// Request processor implementation to be used with <see cref="RateLimiter"/> for
/// supporting <see cref="UberRpcService{TService}"/> service technology.
/// </summary>
/// <typeparam name="TService">Service interface of <see cref="UberRpcService{TService}"/>.</typeparam>
/// <remarks>
/// <para>
/// This processor uses attributes for decorating RPC methods to declare applicable policies for each service API.
/// </para>
/// </remarks>
public sealed class UberRpcRequestProcessor<TService> : IRequestProcessor
    where TService : class
{
    private readonly IPolicyStateProvider _policyStateProvider;
    private readonly Func<UberRpcRequest, Task> _acceptedCallback;
    private readonly Func<UberRpcRequest, Task> _deniedCallback;

    /// <summary>
    /// C'tor.
    /// </summary>
    /// <param name="policyStateProvider">Policy state provider to query for concrete policy implementations.</param>
    /// <param name="accepted">Callback for accepted requests.</param>
    /// <param name="denied">Callback for denied requests.</param>
    public UberRpcRequestProcessor(IPolicyStateProvider policyStateProvider, Func<UberRpcRequest, Task> accepted, Func<UberRpcRequest, Task> denied)
    {
        ArgumentNullException.ThrowIfNull(policyStateProvider);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(denied);

        _policyStateProvider = policyStateProvider;
        _acceptedCallback = accepted;
        _deniedCallback = denied;
    }

    /// <summary>
    /// Called by <see cref="RateLimiter"/> when it accpets request.  This will cause accept callback to be invoked.
    /// </summary>
    /// <param name="request">Request in context.</param>
    /// <param name="cancel">Cancellation token.</param>
    /// <returns>Async.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if request is not of <see cref="UberRpcRequest"/> type.  Shouldn't happen.</exception>
    public async Task AcceptRequest(IRequest request, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uberRpcRequest = request as UberRpcRequest
            ?? throw new ArgumentOutOfRangeException(nameof(request), $"Request is not of {nameof(UberRpcRequest)} type.");
        await _acceptedCallback(uberRpcRequest);
    }

    /// <summary>
    /// Called by <see cref="RateLimiter"/> when it accpets request.  This will cause accept callback to be invoked.
    /// </summary>
    /// <param name="request">Request in context.</param>
    /// <param name="cancel">Cancellation token.</param>
    /// <returns>Async.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if request is not of <see cref="UberRpcRequest"/> type.  Shouldn't happen.</exception>
    public async Task DenyRequest(IRequest request, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uberRpcRequest = request as UberRpcRequest
            ?? throw new ArgumentOutOfRangeException(nameof(request), $"Request is not of {nameof(UberRpcRequest)} type.");
        await _deniedCallback(uberRpcRequest);
    }

    /// <summary>
    /// Returns applicable policies for the request and their applicable scope.
    /// </summary>
    /// <param name="request">Request in context.</param>
    /// <param name="cancel">Cancellation token.</param>
    /// <returns>Enumeration of policies and their scope.</returns>
    /// <exception cref="Exception"></exception>
    public Task<IEnumerable<KeyValuePair<string, RatePolicy>>> GetRequestPolicies(IRequest request, CancellationToken cancel = default)
    {
        if (request is UberRpcRequest uberRpcRequest)
        {
            var methodInfo = typeof(TService).GetMethod(uberRpcRequest.Request.MethodName);
            if (methodInfo == null)
            {
                // Let service itself handle invalid request; service-invalid requests aren't subject to rate policy.
                return Task.FromResult(Enumerable.Empty<KeyValuePair<string, RatePolicy>>());
            }

            return Task.FromResult(methodInfo.GetCustomAttributes<UberRpcRatePolicyBaseAttribute>(true)
                .Where(a => a != null)
                .Select(attribute => {
                    var sb = new StringBuilder();
                    sb.Append(uberRpcRequest.Request.MethodName);
                    sb.Append(':');
                    if (attribute.WithAuthContext && uberRpcRequest.Request.AuthorizationContext.HasValue)
                    {
                        sb.Append(uberRpcRequest.Request.AuthorizationContext);
                        sb.Append(':');
                    }
                    foreach (var argIndex in attribute.ArgIndecies)
                    {
                        if (uberRpcRequest.Request.Args == null || argIndex >= uberRpcRequest.Request.Args.Length)
                        {
                            // TODO: Custom exception type?
                            throw new Exception("Attribute references argument index that is out of range.");
                        }
                        sb.Append(uberRpcRequest.Request.Args[argIndex].GetHashCode());
                        sb.Append(':');
                    }

                    var policy = attribute.GetPolicy(_policyStateProvider);

                    return new KeyValuePair<string, RatePolicy>(sb.ToString(), policy);
                })
            );
        }
        else
        {
            // We can either throw b/c we got request that's not related to this service, or probably
            // better be amiacable here and just return no policies as we don't have anything to do
            // with this request.
            return Task.FromResult(Enumerable.Empty<KeyValuePair<string, RatePolicy>>());
        }
    }
}

/// <summary>
/// Wrapper for <see cref="UberRpcServiceCallMessage"/> request.
/// </summary>
public class UberRpcRequest : IRequest
{
    /// <summary>
    /// Wrapped request.
    /// </summary>
    public readonly UberRpcServiceCallMessage Request;

    /// <summary>
    /// General purpose context dictionary for the request.
    /// </summary>
    public readonly Dictionary<string, dynamic> Context = [];

    public UberRpcRequest(UberRpcServiceCallMessage request)
    {
        Request = request;
    }
}

/// <summary>
/// Base attribute that provides some common properties for all attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public abstract class UberRpcRatePolicyBaseAttribute : Attribute
{
    /// <summary>
    /// Specifies if policy should discriminate on authorization context, i.e. separate
    /// allocation bucket per user, or if single bucket for all users.
    /// </summary>
    public bool WithAuthContext = true;

    /// <summary>
    /// Specifies, by index position, which API methods parameters should be used to discriminate on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each reference argument value is evaluated using its <see cref="object.GetHashCode"/> to be used
    /// to construct a hash to discriminate on that value.  As such, referenced argument
    /// type must produce plausible hashcode of value for correct behavior.
    /// </para>
    /// </remarks>
    public int[] ArgIndecies = [];

    /// <summary>
    /// Get policy that deriving attribute represents.
    /// </summary>
    /// <param name="factory">Policy factory to use to create policy instance.</param>
    /// <returns></returns>
    internal abstract RatePolicy GetPolicy(IPolicyStateProvider factory);
}

/// <summary>
/// Specifies that API is bound to <see cref="ConcurrentWindowRatePolicy"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class ConcurrentWindowRatePolicyAttribute : UberRpcRatePolicyBaseAttribute
{
    /// <summary>
    /// See <see cref="ConcurrentWindowRatePolicy.Degree"/>.
    /// </summary>
    public uint Degree { get; protected set; }

    public ConcurrentWindowRatePolicyAttribute(uint degree)
    {
        Degree = degree;
    }

    internal override RatePolicy GetPolicy(IPolicyStateProvider factory)
    {
        //TODO: opportunity to cache policies via weak reference with same degree and thus reduce number of instances floating around.
        var concurrentPolicy = factory.GetPolicy<ConcurrentWindowRatePolicy>();

        concurrentPolicy.Degree = Degree;
        return concurrentPolicy;
    }
}

/// <summary>
/// Specifies that API is bound to <see cref="FixedWindowRatePolicy"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class FixedWindowRatePolicyAttribute : UberRpcRatePolicyBaseAttribute
{
    /// <summary>
    /// See <see cref="FixedWindowRatePolicy.Period"/>.
    /// </summary>
    public TimeSpan Period { get; protected set; }

    /// <summary>
    /// See <see cref="FixedWindowRatePolicy.PeriodQuantity"/>.
    /// </summary>
    public uint PeriodQuantity { get; protected set; }

    public FixedWindowRatePolicyAttribute(TimeSpan period, uint periodQuantity)
    {
        Period = period;
        PeriodQuantity = periodQuantity;
    }

    // Because https://stackoverflow.com/questions/51000597/c-sharp-a-timespan-in-attribute
    public FixedWindowRatePolicyAttribute(int milliseconds, uint periodQuantity)
        : this (TimeSpan.FromMilliseconds(milliseconds), periodQuantity)
    { }

    internal override RatePolicy GetPolicy(IPolicyStateProvider factory)
    {
        //TODO: opportunity to cache policies via weak reference with same degree and thus reduce number of instances floating around.
        var fixedPolicy = factory.GetPolicy<FixedWindowRatePolicy>();

        fixedPolicy.Period = Period;
        fixedPolicy.PeriodQuantity = PeriodQuantity;
        return fixedPolicy;
    }
}

/// <summary>
/// Specifies that API is bound to <see cref="SlidingWindowRatePolicy"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class SlidingWindowRatePolicyAttribute : UberRpcRatePolicyBaseAttribute
{
    /// <summary>
    /// See <see cref="SlidingWindowRatePolicy.Period"/>.
    /// </summary>
    public TimeSpan Period { get; protected set; }

    /// <summary>
    /// See <see cref="SlidingWindowRatePolicy.PeriodQuantity"/>.
    /// </summary>
    public uint PeriodQuantity { get; protected set; }

    public SlidingWindowRatePolicyAttribute(TimeSpan period, uint periodQuantity)
    {
        Period = period;
        PeriodQuantity = periodQuantity;
    }

    // Because https://stackoverflow.com/questions/51000597/c-sharp-a-timespan-in-attribute
    public SlidingWindowRatePolicyAttribute(int milliseconds, uint periodQuantity)
        : this (TimeSpan.FromMilliseconds(milliseconds), periodQuantity)
    { }

    internal override RatePolicy GetPolicy(IPolicyStateProvider factory)
    {
        //TODO: opportunity to cache policies via weak reference with same degree and thus reduce number of instances floating around.
        var fixedPolicy = factory.GetPolicy<SlidingWindowRatePolicy>();

        fixedPolicy.Period = Period;
        fixedPolicy.PeriodQuantity = PeriodQuantity;
        return fixedPolicy;
    }
}
