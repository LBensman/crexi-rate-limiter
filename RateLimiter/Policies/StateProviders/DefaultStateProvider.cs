using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RateLimiter.Policies.StateProviders;

/// <summary>
/// Policy state provider that uses locally memory for storing policy state.
/// </summary>
/// <remarks>
/// <para>
/// Note that <see cref="DefaultStateProvider"/> is not designed or intended to be used with more than
/// one <see cref="RateLimiter"/> instance in the same process.  Due to use of static members to store
/// data, multiple instances can potentially interact with unintended effects, especially in case where
/// request keys collide.  While limitation exists currently, in the future, it may be eliminated
/// to allow for more flexibility, if it's determined that more than one rate limiter backed by this
/// provider support is needed.
/// </para>
/// </remarks>
public class DefaultStateProvider : IPolicyStateProvider
{
    private static readonly object _dummy = new();  // hack: dummy var to make switch expression on template type work syntactically.

    public struct Options
    {
        public enum PrecisionOption
        {
            Strict,
            Performance
        }

        public PrecisionOption Precision { get; init; }
    }

    private readonly Options _options;

    public DefaultStateProvider() : this(new Options { Precision = Options.PrecisionOption.Strict})
    { }

    public DefaultStateProvider(Options options)
    {
        _options = options;
    }

    public TPolicyType GetPolicy<TPolicyType>() where TPolicyType : RatePolicy
    {
        return (_dummy switch
        {
            object when typeof(TPolicyType) == typeof(Policies.FixedWindowRatePolicy) => new FixedWindowRatePolicy(_options) as TPolicyType,
            object when typeof(TPolicyType) == typeof(Policies.SlidingWindowRatePolicy) => new SlidingWindowRatePolicy(_options) as TPolicyType,
            object when typeof(TPolicyType) == typeof(Policies.ConcurrentWindowRatePolicy) => new ConcurrentWindowRatePolicy() as TPolicyType,
            _ => throw new Exception($"Unsupporeted policy type {typeof(TPolicyType).Name}"),
        })!;
    }

    private sealed class ConcurrentWindowRatePolicy : Policies.ConcurrentWindowRatePolicy
    {
        private static readonly ConcurrentDictionary<string, uint> _map = new();
        public override Task<PolicyLease> ObtainLease(string requestPolicyKey, CancellationToken cancel = default)
        {
            var count = _map.AddOrUpdate(requestPolicyKey, 1, (k, v) => ++v);

            var lease = new PolicyLease(count <= Degree, () =>
            {
                if (0 == _map.AddOrUpdate(requestPolicyKey, 0, (k, v) => --v))
                {
                    _map.TryRemove(KeyValuePair.Create(requestPolicyKey, 0U));
                }
            });

            return Task.FromResult(lease);
        }
    }

    private sealed class FixedWindowRatePolicy : Policies.FixedWindowRatePolicy
    {
        private static readonly ConcurrentDictionary<Tuple<string, TimeSpan>, uint> _performanceMap = new();
        private static readonly Dictionary<Tuple<string, TimeSpan>, Tuple<DateTime, uint>> _strictMap = [];

        private readonly Options _options;

        internal FixedWindowRatePolicy(Options options)
        {
            _options = options;
        }

        public override Task<PolicyLease> ObtainLease(string requestPolicyKey, CancellationToken cancel = default)
        {
            var key = Tuple.Create(requestPolicyKey, Period);
            uint count;

            switch (_options.Precision)
            {
                case Options.PrecisionOption.Performance:
                    count = _performanceMap.AddOrUpdate(key, 1, (k, v) => ++v);

                    if (1 == count)
                    {
                        Task.Run(async () => {
                            await Task.Delay(Period);
                            _performanceMap.TryRemove(key, out _);
                        }, CancellationToken.None);
                    }

                    break;
                case Options.PrecisionOption.Strict:
                    lock (_strictMap)
                    {
                        if (!_strictMap.ContainsKey(key))
                        {
                            count = 1U;
                            _strictMap[key] = Tuple.Create(DateTime.UtcNow + Period, count);
                            Task.Run(async () => {
                                await Task.Delay(Period);
                                lock (_strictMap)
                                {
                                    if (_strictMap.ContainsKey(key))
                                    {
                                        (var time, _) = _strictMap[key];
                                        if(DateTime.UtcNow < time)
                                        {
                                            _strictMap.Remove(key);
                                        }
                                    }
                                }
                            }, CancellationToken.None);
                        }
                        else
                        {
                            (var time, count) = _strictMap[key];
                            if (DateTime.UtcNow < time)
                            {
                                _strictMap[key] = Tuple.Create(time, ++count);
                            }
                            else
                            {
                                count = 1U;
                                _strictMap[key] = Tuple.Create(DateTime.UtcNow + Period, count);
                            }
                        }
                    }
                    break;
                default:
                    throw new NotSupportedException();
            }



            var lease = new PolicyLease(count <= PeriodQuantity);
            return Task.FromResult(lease);
        }
    }

    private sealed class SlidingWindowRatePolicy : Policies.SlidingWindowRatePolicy
    {
        private static readonly ConcurrentDictionary<Tuple<string, TimeSpan>, ConcurrentQueue<DateTime>> _map = new();

        private readonly Options _options;

        internal SlidingWindowRatePolicy(Options options)
        {
            _options = options;
        }

        public override Task<PolicyLease> ObtainLease(string requestPolicyKey, CancellationToken cancel = default)
        {
            var key = Tuple.Create(requestPolicyKey, Period);
            var list = _map.GetOrAdd(key, k => new());
            bool grant;

            bool freshList;

            switch (_options.Precision)
            {
                case  Options.PrecisionOption.Strict:
                    lock (list)
                    {
                        while (list.TryPeek(out var head))
                        {
                            if (head < DateTime.UtcNow)
                            {
                                list.TryDequeue(out _);
                                continue;
                            }

                            break;
                        }

                        grant = list.Count < PeriodQuantity;
                        if (grant)
                        {
                            list.Enqueue(DateTime.UtcNow + Period);
                        }
                    }
                    break;
                case Options.PrecisionOption.Performance:
                    freshList = list.IsEmpty;
                    grant = list.Count < PeriodQuantity;
                    if (grant)
                    {
                        list.Enqueue(DateTime.UtcNow + Period);
                    }
                    if (freshList)
                    {
                        Task.Run(async () => {
                            while(true)
                            {
                                DateTime? next = null;

                                lock(list)
                                {

                                    while (list.TryPeek(out var head))
                                    {
                                        if (head < DateTime.UtcNow)
                                        {
                                            list.TryDequeue(out _);
                                            continue;
                                        }

                                        next = head;

                                        break;
                                    }
                                }

                                if (next.HasValue)
                                {
                                    await Task.Delay(next.Value - DateTime.UtcNow);
                                    continue;
                                }

                                return;
                            }
                        }, CancellationToken.None);
                    }
                    break;
                default:
                    throw new NotSupportedException();
            }

            var lease = new PolicyLease(grant);
            return Task.FromResult(lease);
        }
    }
}