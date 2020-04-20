using System;
using Microsoft.AspNetCore.Http;

namespace Umbraco.Web.Common.Extensions
{
    public static class HttpContextAccessorExtensions
    {
        public static HttpContext GetRequiredHttpContext(this IHttpContextAccessor httpContextAccessor)
        {
            if (httpContextAccessor == null) throw new ArgumentNullException(nameof(httpContextAccessor));
            var httpContext = httpContextAccessor.HttpContext;

            if(httpContext is null) throw new InvalidOperationException("HttpContext is null");

            return httpContext;
        }
    }
}
