using System;
using System.Threading.Tasks;

namespace UberRpcService;

public class UberRpcServiceCallMessage
{
    public required string MethodName { get; init; }
    public int? AuthorizationContext { get; init; }
    public object[]? Args { get; init; }
}

public class UberRpcServiceResponseMessage
{
    public enum ResultCode
    {
        Success,
        Fail,
        NoMethod,
        TooMuch
    }

    public ResultCode ResultStatus { get; init; }

    public object? ReturnValue { get; init; }

    public Exception? Exception { get; init; }
}

public class UberRpcService<TService> where TService : class
{
    private readonly TService _service;

    public UberRpcService(TService service)
    {
        _service = service;
    }

    public async Task<UberRpcServiceResponseMessage> InvokeRpcCall(UberRpcServiceCallMessage message)
    {
        var method = typeof(TService).GetMethod(message.MethodName);
        if (method == null)
        {
            return new UberRpcServiceResponseMessage
            {
                ResultStatus = UberRpcServiceResponseMessage.ResultCode.NoMethod
            };
        }

        try
        {
            var methodReturn = method.Invoke(_service, message.Args);

            if (method.ReturnType.IsAssignableTo(typeof(Task)))
            {
                // Async call
                var methodTask = methodReturn as Task;

                try
                {
                    await methodTask!;
                }
                catch
                { }

                if (!methodTask!.IsCompletedSuccessfully)
                {
                    return new UberRpcServiceResponseMessage
                    {
                        ResultStatus = UberRpcServiceResponseMessage.ResultCode.Fail,
                        Exception = methodTask.Exception
                    };
                }

                if (method.ReturnType.GenericTypeArguments.Length == 1)
                {
                    return new UberRpcServiceResponseMessage
                    {
                        ResultStatus = UberRpcServiceResponseMessage.ResultCode.Success,
                        ReturnValue = ((Task<object>)methodTask).Result
                    };
                }

                return new UberRpcServiceResponseMessage
                {
                    ResultStatus = UberRpcServiceResponseMessage.ResultCode.Success,
                    ReturnValue = null
                };
            }
            else
            {
                // Sync call
                return new UberRpcServiceResponseMessage
                {
                    ResultStatus = UberRpcServiceResponseMessage.ResultCode.Success,
                    ReturnValue = methodReturn
                };
            }
        }
        catch (Exception e)
        {
            return new UberRpcServiceResponseMessage
            {
                ResultStatus = UberRpcServiceResponseMessage.ResultCode.Fail,
                Exception = e
            };
        }
    }
}