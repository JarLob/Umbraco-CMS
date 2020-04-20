﻿using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Core;
using Umbraco.Core.Logging;

namespace Umbraco.Web.Install
{
    /// <summary>
    /// Ensures authorization occurs for the installer if it has already completed.
    /// If install has not yet occurred then the authorization is successful.
    /// </summary>
    public class HttpInstallAuthorizeAttribute : TypeFilterAttribute
    {
        public HttpInstallAuthorizeAttribute() : base(typeof(HttpInstallAuthorizeFilter))
        {
        }

        private class HttpInstallAuthorizeFilter : IAuthorizationFilter
        {
            public void OnAuthorization(AuthorizationFilterContext authorizationFilterContext)
            {
                var serviceProvider = authorizationFilterContext.HttpContext.RequestServices;
                var runtimeState = serviceProvider.GetService<IRuntimeState>();
                var umbracoContext = serviceProvider.GetService<IUmbracoContext>();
                var logger = serviceProvider.GetService<ILogger>();

                if (!IsAllowed(runtimeState, umbracoContext, logger))
                {
                    authorizationFilterContext.Result = new ForbidResult();
                }

            }

            private static bool IsAllowed(IRuntimeState runtimeState, IUmbracoContext umbracoContext, ILogger logger)
            {
                try
                {
                    // if not configured (install or upgrade) then we can continue
                    // otherwise we need to ensure that a user is logged in
                    return runtimeState.Level == RuntimeLevel.Install
                           || runtimeState.Level == RuntimeLevel.Upgrade
                           || umbracoContext.Security.ValidateCurrentUser();
                }
                catch (Exception ex)
                {
                    logger.Error<HttpInstallAuthorizeAttribute>(ex, "An error occurred determining authorization");
                    return false;
                }
            }
        }
    }

}
