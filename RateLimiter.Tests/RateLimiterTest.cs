using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RateLimiter.Policies;
using RateLimiter.Policies.StateProviders;
using RateLimiter.UberRpc;
using UberRpcService;

namespace RateLimiter.Tests;

[TestFixture]
public class RateLimiterTest
{
	class TestRpcService
	{
		public Task PlainCallAsync()
		{
			return Task.CompletedTask;
		}

		private uint _concurrentOneDegreePolicyCalls = 0;
		[ConcurrentWindowRatePolicy(1)]
		public async Task<uint> ConcurrentOneDegreePolicyCall(TimeSpan timeSpan)
		{
			var call = Interlocked.Increment(ref _concurrentOneDegreePolicyCalls);
			await Task.Delay(timeSpan);
			return call;
		}

		private uint _concurrentHighDegreePolicyCalls = 0;
		[ConcurrentWindowRatePolicy(1000, ArgIndecies = [], WithAuthContext = false)]
		public async Task<uint> ConcurrentHighDegreePolicyCall(TimeSpan timeSpan)
		{
			var call = Interlocked.Increment(ref _concurrentHighDegreePolicyCalls);
			await Task.Delay(timeSpan);
			return call;
		}

		private uint _fixedPolicyCalls = 0;
		[FixedWindowRatePolicy(100, 10, ArgIndecies = [], WithAuthContext = false)]
		public async Task<uint> FixedWindowPolicyCall(TimeSpan timeSpan)
		{
			var call = Interlocked.Increment(ref _fixedPolicyCalls);
			await Task.Delay(timeSpan);
			return call;
		}

		private uint _fixedPolicyCallsToo = 0;
		[FixedWindowRatePolicy(100, 10)]
		public async Task<uint> FixedWindowPolicyCallToo(TimeSpan timeSpan)
		{
			var call = Interlocked.Increment(ref _fixedPolicyCallsToo);
			await Task.Delay(timeSpan);
			return call;
		}

		private uint _fixedPolicyWithAuthCalls = 0;
		[FixedWindowRatePolicy(100, 10, ArgIndecies = [], WithAuthContext = true)]
		public async Task<uint> FixedWindowPolicyWithAuthCall(TimeSpan timeSpan)
		{
			var call = Interlocked.Increment(ref _fixedPolicyWithAuthCalls);
			await Task.Delay(timeSpan);
			return call;
		}

		private uint _fixedPolicyNullArgIndeciesCalls = 0;
		[FixedWindowRatePolicy(100, 10)]
		public async Task<uint> FixedWindowPolicyNullArgIndeciesCall(TimeSpan timeSpan)
		{
			var call = Interlocked.Increment(ref _fixedPolicyNullArgIndeciesCalls);
			await Task.Delay(timeSpan);
			return call;
		}

		private uint _fixedPolicyBadArgIndexCalls = 0;
		[FixedWindowRatePolicy(100, 10, ArgIndecies = [10, 20])]
		public async Task<uint> FixedWindowPolicyBadArgIndexCall(TimeSpan timeSpan)
		{
			var call = Interlocked.Increment(ref _fixedPolicyBadArgIndexCalls);
			await Task.Delay(timeSpan);
			return call;
		}

		private uint _slidingPolicyCalls = 0;
		[SlidingWindowRatePolicy(100, 10, ArgIndecies = [], WithAuthContext = false)]
		public async Task<uint> SlidingWindowPolicyCall(TimeSpan timeSpan)
		{
			var call = Interlocked.Increment(ref _slidingPolicyCalls);
			await Task.Delay(timeSpan);
			return call;
		}

		// Test with multiple args
	};

	private static RateLimiter SetupRateLimitedService(Func<UberRpcRequest, Task> accepted, Func<UberRpcRequest, Task> denied)
	{
    	var rpcService = new UberRpcService<TestRpcService>(new TestRpcService());
        var requestProcessor = new UberRpcRequestProcessor<TestRpcService>(
			new DefaultStateProvider(
				new DefaultStateProvider.Options {
					// In these set of tests, we test the strict behavior as it's more precise.
					// testing performance is tricker since, by its proposed nature, the behavior
					// would be approximate and thus stocastic, requiring a more elaborate testing
					// with some statistical analysis that's currently beyond the scope.
					// It's a TODO for the future.
					Precision = DefaultStateProvider.Options.PrecisionOption.Strict
				}),
			accepted, denied
		);
        return new RateLimiter(requestProcessor);
	}


	[Test(Description = "Most basic test that minimally validates that plain call works, without rate limits.")]
	public async Task PlainCallWorks()
	{
		var accepted = false;
		var denied = false;
    	var rpcService = new UberRpcService<TestRpcService>(new TestRpcService());
        var rateLimiter = SetupRateLimitedService(request => {
			accepted = true;
			return Task.CompletedTask;
		}, request => {
			denied = true;
			return Task.CompletedTask;
		});

		var incomingMessage = new UberRpcServiceCallMessage	// i.e. incomingMessage = transport.Read();
		{
			MethodName = nameof(TestRpcService.PlainCallAsync),
		};

		await rateLimiter.CheckRequest(new UberRpcRequest(incomingMessage));

		Assert.That(accepted, Is.True);
		Assert.That(denied, Is.False);
	}

	[Test(Description = $"Validates {nameof(ConcurrentWindowRatePolicy)} of single {nameof(ConcurrentWindowRatePolicy.Degree)} is properly enforced.")]
	public async Task ConcurrentOneDegreeCallWorks()
	{
		const string ContextAccepted = "Accepted";
    	var rpcService = new UberRpcService<TestRpcService>(new TestRpcService());
        var rateLimiter = SetupRateLimitedService(async request => {
			request.Context[ContextAccepted] = true;
			await rpcService.InvokeRpcCall(request.Request);
		}, request => {
			request.Context[ContextAccepted] = false;
			return Task.CompletedTask;
		});

		var incomingMessage1 = new UberRpcServiceCallMessage	// i.e. incomingMessage = transport.Read();
		{
			MethodName = nameof(TestRpcService.ConcurrentOneDegreePolicyCall),
			Args = [TimeSpan.FromMilliseconds(100)]
		};
		var incomingMessage2 = new UberRpcServiceCallMessage	// i.e. incomingMessage = transport.Read();
		{
			MethodName = nameof(TestRpcService.ConcurrentOneDegreePolicyCall),
			Args = [TimeSpan.FromMilliseconds(100)]
		};

        UberRpcRequest request1 = new(incomingMessage1);
        UberRpcRequest request2 = new(incomingMessage2);
        var call1Task = rateLimiter.CheckRequest(request1);
        var call2Task = rateLimiter.CheckRequest(request2);

		await Task.WhenAll(call1Task, call2Task);

		Assert.That(request1.Context[ContextAccepted], Is.True);
		Assert.That(request2.Context[ContextAccepted], Is.False);
	}

	[Test(Description = $"Validates {nameof(ConcurrentWindowRatePolicy)} of some high {nameof(ConcurrentWindowRatePolicy.Degree)} is properly enforced.")]
	public async Task ConcurrentHighDegreeCallWorks()
	{
		const string ContextAccepted = "Accepted";
		const string ContextSequence = "Sequence";
		var PolicyHighDegree = typeof(TestRpcService)
			.GetMethod(nameof(TestRpcService.ConcurrentHighDegreePolicyCall))!
			.GetCustomAttribute<ConcurrentWindowRatePolicyAttribute>()!
			.Degree;
    	var rpcService = new UberRpcService<TestRpcService>(new TestRpcService());
        var rateLimiter = SetupRateLimitedService(async request => {
			request.Context[ContextAccepted] = true;
			await rpcService.InvokeRpcCall(request.Request);
		}, request => {
			request.Context[ContextAccepted] = false;
			return Task.CompletedTask;
		});

		var requestTasks = Enumerable.Range(0, (int)(PolicyHighDegree * 2.5))
			.Select(n => {
				var request = new UberRpcRequest (new UberRpcServiceCallMessage {
					MethodName = nameof(TestRpcService.ConcurrentHighDegreePolicyCall),
					Args = [TimeSpan.FromMilliseconds(n % 2 == 0 ? 100 : 200)],
				});
				request.Context[ContextSequence] = n;
				return request;
			})
			.Select(async r => {
				await Task.Delay(r.Context[ContextSequence] < PolicyHighDegree * 2 ? 0 : 150);
				await rateLimiter.CheckRequest(r);
				return r;
			}).ToArray();

		await Task.WhenAll(requestTasks);

		var requests = requestTasks.Select(t => t.Result).ToArray();

		for (var i = 0; i < PolicyHighDegree; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.True);
		}
		for (var i = PolicyHighDegree; i < PolicyHighDegree * 2; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.False);
		}
		for (var i = PolicyHighDegree * 2; i < (int)(PolicyHighDegree * 2.5); ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.True);
		}
	}

	[Test(Description = $"Validates {nameof(FixedWindowRatePolicy)} of some {nameof(FixedWindowRatePolicy.PeriodQuantity)} over {nameof(FixedWindowRatePolicy.Period)} is properly enforced.")]
	public async Task FixedWindowCallWorks()
	{
		const string ContextAccepted = "Accepted";
		const string ContextSequence = "Sequence";
		var attribute = typeof(TestRpcService)
			.GetMethod(nameof(TestRpcService.FixedWindowPolicyCall))!
			.GetCustomAttribute<FixedWindowRatePolicyAttribute>()!;
		var PeriodQuantity = attribute.PeriodQuantity;
		var Period = attribute.Period;
    	var rpcService = new UberRpcService<TestRpcService>(new TestRpcService());
        var rateLimiter = SetupRateLimitedService(async request => {
			request.Context[ContextAccepted] = true;
			await rpcService.InvokeRpcCall(request.Request);
		}, request => {
			request.Context[ContextAccepted] = false;
			return Task.CompletedTask;
		});

		var requestTasks = Enumerable.Range(0, (int)(PeriodQuantity * 3))
			.Select(n => {
				var request = new UberRpcRequest (new UberRpcServiceCallMessage {
					MethodName = nameof(TestRpcService.FixedWindowPolicyCall),
					Args = [Period],
				});
				request.Context[ContextSequence] = n;
				return request;
			})
			.Select(async r => {
				await Task.Delay(r.Context[ContextSequence] < PeriodQuantity * 2 ? TimeSpan.Zero : Period);
				await rateLimiter.CheckRequest(r);
				return r;
			}).ToArray();

		await Task.WhenAll(requestTasks);

		var requests = requestTasks.Select(t => t.Result).ToArray();

		for (var i = 0; i < PeriodQuantity; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.True);
		}
		for (var i = PeriodQuantity; i < PeriodQuantity * 2; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.False);
		}
		for (var i = PeriodQuantity * 2; i < (int)(PeriodQuantity * 3); ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.True);
		}
	}

	[Test( Description = $"Validates {nameof(SlidingWindowRatePolicy)} of some {nameof(SlidingWindowRatePolicy.PeriodQuantity)} over {nameof(SlidingWindowRatePolicy.Period)} is properly enforced.")]
	[Ignore("This test is timing sensitive to and highly influenced by thread/task scheduling, giving unstable results.  It needs Fakes library to control for DateTime values")]
	public async Task SlidingWindowCallWorks()
	{
		const string ContextAccepted = "Accepted";
		const string ContextSequence = "Sequence";
		var attribute = typeof(TestRpcService)
			.GetMethod(nameof(TestRpcService.SlidingWindowPolicyCall))!
			.GetCustomAttribute<SlidingWindowRatePolicyAttribute>()!;
		var PeriodQuantity = attribute.PeriodQuantity;
		var Period = attribute.Period;
    	var rpcService = new UberRpcService<TestRpcService>(new TestRpcService());
        var rateLimiter = SetupRateLimitedService(async request => {
			request.Context[ContextAccepted] = true;
			await rpcService.InvokeRpcCall(request.Request);
		}, request => {
			request.Context[ContextAccepted] = false;
			return Task.CompletedTask;
		});

		var requestTasks = Enumerable.Range(0, (int)(PeriodQuantity * 3))
			.Select(n => {
				var request = new UberRpcRequest (new UberRpcServiceCallMessage {
					MethodName = nameof(TestRpcService.SlidingWindowPolicyCall),
					Args = [Period],
				});
				request.Context[ContextSequence] = n;
				return request;
			})
			.Select(async r => {
                var sequence = (int)r.Context[ContextSequence];
                var delay = sequence * Period / PeriodQuantity / (sequence < PeriodQuantity * 2 ? 2 : 4)
						    + (sequence < PeriodQuantity * 2 ? TimeSpan.Zero : (Period / 2));
                await Task.Delay(delay);
				await rateLimiter.CheckRequest(r);
				return r;
			}).ToArray();

		await Task.WhenAll(requestTasks);

		var requests = requestTasks.Select(t => t.Result).ToArray();

		for (var i = 0; i < PeriodQuantity; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.True);
		}
		for (var i = PeriodQuantity; i < PeriodQuantity * 2; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.False);
		}

		int allowed = 0;
		for (var i = PeriodQuantity * 2; i < (int)(PeriodQuantity * 3); ++i)
		{
			allowed += requests[i].Context[ContextAccepted];
		}

		Assert.That(allowed, Is.EqualTo(PeriodQuantity / 2));
	}

	[Test(Description = $"Validates {nameof(FixedWindowRatePolicy)} property differentiates two separate methods with the same policy.")]
	public async Task PolicyDiscriminatesOnMethodsCorrectly()
	{
		const string ContextAccepted = "Accepted";
		const string ContextSequence = "Sequence";
		var attribute = typeof(TestRpcService)
			.GetMethod(nameof(TestRpcService.FixedWindowPolicyCall))!
			.GetCustomAttribute<FixedWindowRatePolicyAttribute>()!;
		var PeriodQuantity = attribute.PeriodQuantity;
		var Period = attribute.Period;
    	var rpcService = new UberRpcService<TestRpcService>(new TestRpcService());
        var rateLimiter = SetupRateLimitedService(async request => {
			request.Context[ContextAccepted] = true;
			await rpcService.InvokeRpcCall(request.Request);
		}, request => {
			request.Context[ContextAccepted] = false;
			return Task.CompletedTask;
		});

		var requestTasks = Enumerable.Range(0, (int)(PeriodQuantity * 3))
			.SelectMany(n => {
				var request = new UberRpcRequest (new UberRpcServiceCallMessage {
					MethodName =nameof(TestRpcService.FixedWindowPolicyCall),
					Args = [Period],
				});
				request.Context[ContextSequence] = n;
				var requestToo = new UberRpcRequest (new UberRpcServiceCallMessage {
					MethodName = nameof(TestRpcService.FixedWindowPolicyCallToo),
					Args = [Period],
				});
				requestToo.Context[ContextSequence] = n;
				return new[] { request, requestToo };
			})
			.Select(async r => {
				await Task.Delay(r.Context[ContextSequence] < PeriodQuantity * 2 ? TimeSpan.Zero : Period);
				await rateLimiter.CheckRequest(r);
				return r;
			}).ToArray();

		await Task.WhenAll(requestTasks);

		var requests = requestTasks.Select(t => t.Result).ToArray();

		for (var i = 0; i < PeriodQuantity * 2; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.True);
		}
		for (var i = PeriodQuantity * 2; i < PeriodQuantity * 4; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.False);
		}
		for (var i = PeriodQuantity * 4; i < PeriodQuantity * 6; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.True);
		}
	}

	[Test(Description = $"Validates {nameof(FixedWindowRatePolicy)} property differentiates two separate auth contexts with the same policy.")]
	public async Task PolicyDiscriminatesOnAuthContextCorrectly()
	{
		const string ContextAccepted = "Accepted";
		const string ContextSequence = "Sequence";
		var attribute = typeof(TestRpcService)
			.GetMethod(nameof(TestRpcService.FixedWindowPolicyWithAuthCall))!
			.GetCustomAttribute<FixedWindowRatePolicyAttribute>()!;
		var PeriodQuantity = attribute.PeriodQuantity;
		var Period = attribute.Period;
    	var rpcService = new UberRpcService<TestRpcService>(new TestRpcService());
        var rateLimiter = SetupRateLimitedService(async request => {
			request.Context[ContextAccepted] = true;
			await rpcService.InvokeRpcCall(request.Request);
		}, request => {
			request.Context[ContextAccepted] = false;
			return Task.CompletedTask;
		});

		var requestTasks = Enumerable.Range(0, (int)(PeriodQuantity * 6))
			.Select(n => {
				var request = new UberRpcRequest (new UberRpcServiceCallMessage {
					MethodName =nameof(TestRpcService.FixedWindowPolicyWithAuthCall),
					AuthorizationContext = n % 2,
					Args = [Period],
				});
				request.Context[ContextSequence] = n;
				return request;
			})
			.Select(async r => {
				await Task.Delay(r.Context[ContextSequence] < PeriodQuantity * 4 ? TimeSpan.Zero : Period);
				await rateLimiter.CheckRequest(r);
				return r;
			}).ToArray();

		await Task.WhenAll(requestTasks);

		var requests = requestTasks.Select(t => t.Result).ToArray();

		for (var i = 0; i < PeriodQuantity * 2; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.True);
		}
		for (var i = PeriodQuantity * 2; i < PeriodQuantity * 4; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.False);
		}
		for (var i = PeriodQuantity * 4; i < PeriodQuantity * 6; ++i)
		{
			Assert.That(requests[i].Context[ContextAccepted], Is.True);
		}
	}

	[Test(Description = $"Validates {nameof(FixedWindowRatePolicy)} property handles attribute with no indecies mentioned.")]
	public async Task PolicyHandlesNoArgIndeciesMentionedCorrectly()
	{
		const string ContextAccepted = "Accepted";
		var attribute = typeof(TestRpcService)
			.GetMethod(nameof(TestRpcService.FixedWindowPolicyNullArgIndeciesCall))!
			.GetCustomAttribute<FixedWindowRatePolicyAttribute>()!;
		var PeriodQuantity = attribute.PeriodQuantity;
		var Period = attribute.Period;
    	var rpcService = new UberRpcService<TestRpcService>(new TestRpcService());
        var rateLimiter = SetupRateLimitedService(async request => {
			request.Context[ContextAccepted] = true;
			await rpcService.InvokeRpcCall(request.Request);
		}, request => {
			request.Context[ContextAccepted] = false;
			return Task.CompletedTask;
		});

		var request = new UberRpcRequest (new UberRpcServiceCallMessage {
			MethodName =nameof(TestRpcService.FixedWindowPolicyNullArgIndeciesCall),
			Args = [Period],
		});

		await rateLimiter.CheckRequest(request);

		Assert.That(request.Context[ContextAccepted], Is.True);
	}

	[Test(Description = $"Validates {nameof(FixedWindowRatePolicy)} property handles attribute with out of range arg index specified.")]
	public void PolicyHandlesBadArgIndexCorrectly()
	{
		const string ContextAccepted = "Accepted";
		var attribute = typeof(TestRpcService)
			.GetMethod(nameof(TestRpcService.FixedWindowPolicyBadArgIndexCall))!
			.GetCustomAttribute<FixedWindowRatePolicyAttribute>()!;
		var PeriodQuantity = attribute.PeriodQuantity;
		var Period = attribute.Period;
    	var rpcService = new UberRpcService<TestRpcService>(new TestRpcService());
        var rateLimiter = SetupRateLimitedService(async request => {
			request.Context[ContextAccepted] = true;
			await rpcService.InvokeRpcCall(request.Request);
		}, request => {
			request.Context[ContextAccepted] = false;
			return Task.CompletedTask;
		});

		var request = new UberRpcRequest (new UberRpcServiceCallMessage {
			MethodName =nameof(TestRpcService.FixedWindowPolicyBadArgIndexCall),
			Args = [Period],
		});

		Assert.ThrowsAsync<Exception>(() =>
			rateLimiter.CheckRequest(request)
		);
	}
}