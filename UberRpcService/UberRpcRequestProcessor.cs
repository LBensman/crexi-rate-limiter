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

public sealed class UberRpcRequestProcessor<TService> : IRequestProcessor
    where TService : class
{
    private readonly IPolicyStateProvider _policyStateProvider;
    private readonly Func<UberRpcRequest, Task> _acceptedCallback;
    private readonly Func<UberRpcRequest, Task> _deniedCallback;

    public UberRpcRequestProcessor(IPolicyStateProvider policyStateProvider, Func<UberRpcRequest, Task> accepted, Func<UberRpcRequest, Task> denied)
    {
        ArgumentNullException.ThrowIfNull(policyStateProvider);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(denied);

        _policyStateProvider = policyStateProvider;
        _acceptedCallback = accepted;
        _deniedCallback = denied;
    }

    public async Task AcceptRequest(IRequest request, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uberRpcRequest = request as UberRpcRequest
            ?? throw new ArgumentOutOfRangeException(nameof(request), $"Request is not of {nameof(UberRpcRequest)} type.");
        await _acceptedCallback(uberRpcRequest);
    }

    public async Task DenyRequest(IRequest request, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uberRpcRequest = request as UberRpcRequest
            ?? throw new ArgumentOutOfRangeException(nameof(request), $"Request is not of {nameof(UberRpcRequest)} type.");
        await _deniedCallback(uberRpcRequest);
    }

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

public class UberRpcRequest : IRequest
{
    public readonly UberRpcServiceCallMessage Request;

    public readonly Dictionary<string, dynamic> Context = [];

    public UberRpcRequest(UberRpcServiceCallMessage request)
    {
        Request = request;
    }
}


[AttributeUsage(AttributeTargets.Method)]
public abstract class UberRpcRatePolicyBaseAttribute : Attribute
{
    public bool WithAuthContext = true;

    public int[] ArgIndecies = [];

    internal Type PolicyType { get; private set; }

    protected UberRpcRatePolicyBaseAttribute(Type policyType)
    {
        PolicyType = policyType;
    }

    internal abstract RatePolicy GetPolicy(IPolicyStateProvider factory);
}

[AttributeUsage(AttributeTargets.Method)]
public class ConcurrentWindowRatePolicyAttribute : UberRpcRatePolicyBaseAttribute
{
    public uint Degree { get; protected set; }

    public ConcurrentWindowRatePolicyAttribute(uint degree)
        : base(typeof(ConcurrentWindowRatePolicy))
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

[AttributeUsage(AttributeTargets.Method)]
public class FixedWindowRatePolicyAttribute : UberRpcRatePolicyBaseAttribute
{
    public TimeSpan Period { get; protected set; }

    public uint PeriodQuantity { get; protected set; }

    public FixedWindowRatePolicyAttribute(TimeSpan period, uint periodQuantity)
        : base(typeof(FixedWindowRatePolicy))
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


[AttributeUsage(AttributeTargets.Method)]
public class SlidingWindowRatePolicyAttribute : UberRpcRatePolicyBaseAttribute
{
    public TimeSpan Period { get; protected set; }

    public uint PeriodQuantity { get; protected set; }

    public SlidingWindowRatePolicyAttribute(TimeSpan period, uint periodQuantity)
        : base(typeof(SlidingWindowRatePolicy))
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
