# Rate Limiting library for .NET

Below is the description of the proposed solution.  The <a href="#original-problem-statement">original poblem statement</a> is retained for reference further below.

## Proposed Solution

### Considerations

In approaching the problem, the following considerations, assumptions and acknowledged limitations have been considered:

* Generic framework<br>
  Rate-limiting is frequently associated with web frameworks in handling of incoming HTTP requests.  However, it's not necessarily limited to such domain, and the need can manifest itself in other communication contexts, such as:

    * RPC (e.g. gRPC, XML-RPC, etc.)
    * Message-oriented protocols (e.g. IRC-like chats)
    * Database engines (e.g. some SQL db engine using rate limiter as query and execution governor limiting the rate a client can submit queries)
    * Even in telephony, limiting incoming call volume and frequency of calls from a particular caller
    * ... and so on.

  Application contexts are unlimited.  As such, the proposed solution attempts to make no assumption of contex, and strives to be as generic as possible.  It sets up a general execution framework, while leaving specifics of applied domain to be delegated to a higher implementation layer via extensibility.

  One example of such consideration is leaving it out to processor to decide how to handle approved and denied requests.  In the hypothetical reference example of `UberRpcRequestProcessor`, I chose to use lambda callbacks for communicating back evaluation of the request.  But I could've chosen to use `event` C# mechanism, or leave it to decorate request with properties describing the result.  But I explicitly avoided defining such contract in `IRequestProcessor` interface -- to be most generic and make least assumptions as possible.
* Asynchronous support<br />
  It may appear that `async`/`await` is used without justified need, as library doesn't itself deal with any IO or similar operations that would justify use of async model.  However, pursuant to consideration stated earlier, it does so to allow implementors and extensions to perform asynchronous operation if their needs so require.
* Applicaction adaptability and flexibility<br />
  Processor really defines the framework of how policies are declared, managed, configured, etc. For some applications it may make sense to have hard attributes, in others, like in ASP.NET, using named attributes with preconfigured policy templates, yet in others maybe processor wants to have some kind of callback to custom evaluate each potential query for a very fluid and dynamic policy specification (e.g. realtime datastore query to see which target points points are configured how at this precise point in time.), etc..  IOW, this library doesn't impose a particular framework for how to specify and manage policy mappings, and leaving it to the specific processor adapter that's written to specifically plug into a particular framework or service natively.
* Distributed support<br />
  Consideration here is that modern systems typically have more than one machine in the pool to handle services (i.e. a web farm), and thus rate limiter use may be "global" to cover the whole service pool as a single rate-limited entity.  As such, library supports extensibility for data store beyond local memory, albeit the default example `DefaultStateProvider` is limited to local memory of a single box. Nevertheless, it should be easy to see how another state provider can be made to utilize SQL Server, memcached, Reddis, or other applicable storage and/or caching technologies that would allow for distributed/scaled support across farms.

### Design

The design can generally be seen broken down in 3 major parts:

* `RateLimiter`, the main class to which a request is passed for rate limiting evaluation.  It is generic and non-service specific.  It uses service-specific implementation of RequestProcessor for interoperability with applicable service.
* RequestProcessor, a class that implements `IRequestProcessor` that has the following responsibilities.
  * Integration into service context and injection of its request handling in service's request handling pipeline.
  * Passing allowed requests to service and handling of rejected requests in service meaningful way.
  * Mapping of service's way of marking of applicable policies to requests and provided services, e.g. API endpoints.<br />
    For example, one specific implementation may use attribute decorations on handling methods to specify endpoints' applicable policies. Yet another may use attributes to decorate message payload types or possibly message payload fields to describe applicable policies.  Yet another way may be to have a registry generated on startup that provides mapping lookups at runtime for applicable policies. It all depends on the what is meaningful to the underlying services for which provider is written and how implementers chose to handle the policy references.
* PolicyStateProvidera class that implements `IPolicyStateProvider` that is responsible for providing storage mechanism for policies' state and concrete implementation of general policy types.

`RateLimiter` is instantiated with a specific RequestProcessor for specific service.

RequestProcessor knows how to integrate into its specific service, how to handle accepted and rejected requests, and how to create mapping of endpoints and policies.  It also uses state provider for policy data store needs.  See example implementation `UberRpcServiceProcessor` for a hypothetical `UberRpcService` service.

#### Extensibility

Extensibility is achieved by keeping `RateLimiter` bare bones, deferring customization to the processor and state providers.

Besides the provided basic policies, users can define additional policy types by simply extening from `RatePolicy`, and extending state provider to provide implementation of evaluation of that policy.

The state provider itself is expected to be written such that it can be extended in some form.

##### New Services

After you had a chance to see how UberRpc is handled, now lets suppose that we want to use `RateLimter` with a new QuantumService.  This `QuantumServiceProcessor` would be a new processor (defined in some new library/project) that knows how to plug in into QuantumService.  However, quantrum world being bizarre, we now need a new policy kind that is not already provided by current solution.  Let's call this new policy `SchrödingerCatRatePolicy` and derive it from RatePolicy, and give it a property `WaveFunction` of some relevant type.

For now, we'll be happy with running on a single box and thus happy with `DefaultStateProvider` that just uses local memory.  However, this provider doesn't know how to handle `SchrödingerCatRatePolicy`. Thus we can create new `QuantumLocalMemoryStateProvider` and derive it from `DefaultStateProvider` so that we don't need to reimplement existing policy kinds.

In `QuantumLocalMemoryStateProvider` we'd `override GetPolicy<TPolicyType>()` to recognize new policy and provide concrete implementation, and call base for all others.  The concrete `ConcreteSchrödingerCatRatePolicy` will then have implementation of its logic of evaluating and collapsing policy's `WaveFunction` to determine the fate of the <strike>cat</strike> request, and memory for storing necessary data to perform its evaluations.

Thus, we now provided new service support, and provided support for new policy type by extending existing code and only providing the additional, marginal functionality necessary for the new service.

##### New State Stores

Local memory-based `DefaultStateProvider` is just a default, simple provider, but not necessarily suitable for service farms, since policy state is local to each member of the farm.

It's possible, however, to extend rate limiter by implementing new state providers.  Some such candidates are ones that use SQLServer, PostgreSQL, Reddis, Memcached, etc.

Such implementations would, at minimum, need to provide concrete implementations for the basic policies, so as to know how to evaluate them using data stored in the respective technologies.

When creating such new state providers, implementors should consider extensibility considerations in-turn, such as the example above where `SchrödingerCatRatePolicy` may be desired to work with non-local memory but with new state provider, and that new state provider can in-turn be extended to add support for `SchrödingerCatRatePolicy` while reusing all other policies.

### Limitations
* There are a lot more unit tests possible, and had to limit to basic, illustrative ones for time reasons.
* Some test cases aren't supported, due to their fuzzy nature.  E.g. library hints for light support for efficiency mode to avoid locking at expense of imprecise results.  Performing statistical testing to cover these is beyond scope.  Some can be handled using the Fakes lib, which, too, kept out of scope.  But, given the nature of the middleware and desire for middleware to be efficient, I did give a sample code on how rate limiter library can be made flexible and allow for flexible tradeoffs.
* `DefaultDataStore` internally assumes that it's the only instance in the process, and only one `RateLimiter` using it exists.  It's not, however a hard limitation, and it could be changed with a bit more code to not have this limitation in the future.

## Original Problem Statement
**Rate-limiting pattern**

Rate limiting involves restricting the number of requests that a client can make.
A client is identified with an access token, which is used for every request to a resource.
To prevent abuse of the server, APIs enforce rate-limiting techniques.
The rate-limiting application can decide whether to allow the request based on the client.
The client makes an API call to a particular resource; the server checks whether the request for this client is within the limit.
If the request is within the limit, then the request goes through.
Otherwise, the API call is restricted.

Some examples of request-limiting rules (you could imagine any others)
* X requests per timespan;
* a certain timespan has passed since the last call;
* For US-based tokens, we use X requests per timespan; for EU-based tokens, a certain timespan has passed since the last call.

The goal is to design a class(-es) that manages each API resource's rate limits by a set of provided *configurable and extendable* rules. For example, for one resource, you could configure the limiter to use Rule A; for another one - Rule B; for a third one - both A + B, etc. Any combination of rules should be possible; keep this fact in mind when designing the classes.

We're more interested in the design itself than in some intelligent and tricky rate-limiting algorithm. There is no need to use a database (in-memory storage is fine) or any web framework. Do not waste time on preparing complex environment, reusable class library covered by a set of tests is more than enough.

There is a Test Project set up for you to use. However, you are welcome to create your own test project and use whatever test runner you like.

You are welcome to ask any questions regarding the requirements—treat us as product owners, analysts, or whoever knows the business.
If you have any questions or concerns, please submit them as a [GitHub issue](https://github.com/crexi-dev/rate-limiter/issues).

You should [fork](https://help.github.com/en/github/getting-started-with-github/fork-a-repo) the project and [create a pull request](https://help.github.com/en/github/collaborating-with-issues-and-pull-requests/creating-a-pull-request-from-a-fork) named as `FirstName LastName` once you are finished.

Good luck!
