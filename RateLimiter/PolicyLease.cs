using System;
using System.Threading.Tasks;

namespace RateLimiter;
/// <summary>
/// Lease returned by <see cref="Policies.RatePolicy.ObtainLease(string, System.Threading.CancellationToken)">.
/// </summary>
/// <remarks>
/// <para>
/// Lease represents two things.  One, it represents policy's answer to the query if a request is allowed
/// by the policy or if policy wishes to limit the request.  Second, it represents a borrowing of policy's
/// allowance for allowed leases for the duration of the request, whereby once request processing
/// is completed, lease is disposed, returning policy's allocation back to the pool to be available
/// for other requests. The return of the lease back to the pool is accomplished via disposal
/// infrastructure by calling <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </para>
/// </remarks>
public class PolicyLease : IAsyncDisposable
    {
        /// <summary>
        /// Indicates if policy has granted an allocation for the request (<c>true</c>), or denied it (<c>false</c>).
        /// </summary>
        public bool IsGranted { get; private set; }

        private readonly Func<Task>? _onRelease;

        /// <summary>
        /// Constructs lease, indicating if it was granted, and synchronous callback to call on disposal.
        /// </summary>
        /// <param name="granted">Indicates if lease is granted.</param>
        /// <param name="onRelease">Callback to release lease.</param>
        public PolicyLease(bool granted, Action onRelease)
            : this(granted, () => { onRelease?.Invoke(); return Task.CompletedTask; })
        { }

        /// <summary>
        /// Constructs lease, indicating if it was granted, and asynchronous callback to call on disposal.
        /// </summary>
        /// <param name="granted">Indicates if lease is granted.</param>
        /// <param name="onRelease">Callback to release lease.</param>
        public PolicyLease(bool granted, Func<Task> onRelease)
            : this(granted)
        {
            _onRelease = onRelease;
        }

        /// <summary>
        /// Constructs lease, indicating if it was granted.
        /// </summary>
        /// <param name="granted">Indicates if lease is granted.</param>
        public PolicyLease(bool granted)
        {
            IsGranted = granted;
        }

        /// <summary>
        /// Releases the lease, and calls release callback if one was passed in a constructor.
        /// </summary>
        /// <returns></returns>
        public virtual async ValueTask DisposeAsync()
        {
            await (_onRelease?.Invoke() ?? ValueTask.CompletedTask.AsTask());
        }
    }