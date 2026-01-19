using CryptoExchange.Net.Objects.Errors;

namespace CryptoManager.Net.Models.Response
{
    public class ApiResult
    {
        public bool Success { get; set; }
        public IEnumerable<ApiError> Errors { get; set; }

        protected ApiResult(bool success, IEnumerable<ApiError> errors)
        {
            Success = success;
            Errors = errors;
        }

        public static ApiResult Ok() => new ApiResult(true, []);
        public static ApiResult Error(ErrorType type, string? code, string? message) => new ApiResult(false, [new ApiError(type, code, message)]);
        public static ApiResult Error(ApiError error) => new ApiResult(false, [error]);
        public static ApiResult Error(IEnumerable<ApiError> errors) => new ApiResult(false, errors);
    }

    public class ApiResult<T> : ApiResult
    {
        public T? Data { get; set; }

        protected ApiResult(bool success, T? data, IEnumerable<ApiError> errors) : base(success, errors)
        {
            Data = data;
        }

        public static ApiResult<T> Ok(T data) => new ApiResult<T>(true, data, []);
        public static new ApiResult<T> Error(ErrorType type, string? code, string? message) => new ApiResult<T>(false, default, [new ApiError(type, code, message)]);
        public static new ApiResult<T> Error(ApiError error) => new ApiResult<T>(false, default, [error]);
        public static new ApiResult<T> Error(IEnumerable<ApiError> errors) => new ApiResult<T>(false, default, errors);
    }

    public class ApiResultPaged<T> : ApiResult<T>
    {
        public int? Page { get; set; }
        public int? PageSize { get; set; }
        public int? Total { get; set; }

        protected ApiResultPaged(bool success, int? page, int? pageSize, int? total, T? data, IEnumerable<ApiError> errors) : base(success, data, errors)
        {
            Page = page;
            PageSize = pageSize;
            Total = total;
        }

        public static ApiResultPaged<T> Ok(int page, int pageSize, int total, T data) => new ApiResultPaged<T>(true, page, pageSize, total, data, []);
        public static new ApiResultPaged<T> Error(ErrorType type, string? code, string? message) => new ApiResultPaged<T>(false, null, null, null, default, [new ApiError(type, code, message)]);
        public static new ApiResultPaged<T> Error(ApiError error) => new ApiResultPaged<T>(false, null, null, null, default, [error]);
        public static new ApiResultPaged<T> Error(IEnumerable<ApiError> errors) => new ApiResultPaged<T>(false, null, null, null, default, errors);
    }

    public class ApiError
    {
        public string? Code { get; set; }
        public string? Message { get; set; }
        public ErrorType Type { get; set; }

        public ApiError(ErrorType type, string? code, string? message)
        {
            Type = type;
            Code = code;
            Message = message;
        }
    }
}
