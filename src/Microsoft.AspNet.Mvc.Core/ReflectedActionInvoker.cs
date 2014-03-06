﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNet.Abstractions;
using Microsoft.AspNet.Mvc.Internal;
using Microsoft.AspNet.DependencyInjection;
using Microsoft.AspNet.Mvc.ModelBinding;
using Microsoft.AspNet.DependencyInjection.NestedProviders;
using Microsoft.AspNet.Mvc.Filters;

namespace Microsoft.AspNet.Mvc
{
    public class ReflectedActionInvoker : IActionInvoker
    {
        private readonly ActionContext _actionContext;
        private readonly ReflectedActionDescriptor _descriptor;
        private readonly IActionResultFactory _actionResultFactory;
        private readonly IServiceProvider _serviceProvider;
        private readonly IControllerFactory _controllerFactory;
        private readonly IActionBindingContextProvider _bindingProvider;
        private readonly INestedProviderManager<FilterProviderContext> _filterProvider;

        public ReflectedActionInvoker(ActionContext actionContext,
                                      ReflectedActionDescriptor descriptor,
                                      IActionResultFactory actionResultFactory,
                                      IControllerFactory controllerFactory,
                                      IActionBindingContextProvider bindingContextProvider,
                                      INestedProviderManager<FilterProviderContext> filterProvider,
                                      IServiceProvider serviceProvider)
        {
            _actionContext = actionContext;
            _descriptor = descriptor;
            _actionResultFactory = actionResultFactory;
            _controllerFactory = controllerFactory;
            _bindingProvider = bindingContextProvider;
            _filterProvider = filterProvider;

            _serviceProvider = serviceProvider;
        }

        public async Task InvokeActionAsync()
        {
            IActionResult actionResult;
            var context = new FilterProviderContext(_descriptor);
            _filterProvider.Invoke(context);

            var modelState = new ModelStateDictionary();
            object controller = _controllerFactory.CreateController(_actionContext, modelState);

            if (controller == null)
            {
                actionResult = new HttpStatusCodeResult(404);
            }
            else
            {
                var method = _descriptor.MethodInfo;

                if (method == null)
                {
                    actionResult = new HttpStatusCodeResult(404);
                }
                else
                {
                    var parameterValues = await GetParameterValues(modelState);

                    var authZFilters = context.AuthorizationFilters;

                    bool authZPassed;
                    if (authZFilters != null && authZFilters.Count > 0)
                    {
                        var authZEndPoint = new AuthorizationFilterEndPoint();
                        authZFilters.Add(authZEndPoint);
                        var authZContext = new AuthorizationFilterContext(_actionContext);
                        var authZPipeline = new FilterPipelineBuilder<AuthorizationFilterContext>(authZFilters,
                            authZContext);

                        await authZPipeline.InvokeAsync();

                        if (authZContext.ActionResult == null &&
                            !authZContext.HasFailed &&
                            authZEndPoint.EndPointCalled)
                        {
                            actionResult = null;
                        }
                        else
                        {
                            actionResult = authZContext.ActionResult ?? new HttpStatusCodeResult(401);                            
                        }
                    }
                    else
                    {
                        actionResult = null;
                    }

                    if (actionResult == null)
                    {
                        var actionFilters = context.ActionFilters ?? new List<IActionFilter>();
                        var actionFilterContext = new ActionFilterContext(_actionContext,
                                                                          parameterValues,
                                                                          method.ReturnType);

                        // TODO: This is extremely temporary and is going to get soon replaced with the action executer
                        var actionEndPoint = new ReflectedActionFilterEndPoint(async (inArray) => method.Invoke(controller, inArray),
                                                                                _actionResultFactory);

                        actionFilters.Add(actionEndPoint);

                        var actionFilterPipeline = new FilterPipelineBuilder<ActionFilterContext>(actionFilters,
                            actionFilterContext);

                        await actionFilterPipeline.InvokeAsync();

                        actionResult = (IActionResult)actionFilterContext.Result;
                    }
                }
            }

            var actionResultFilters = context.ActionResultFilters ?? new List<IActionResultFilter>();
            var actionResultFilterContext = new ActionResultFilterContext(_actionContext, actionResult);
            var actionResultFilterEndPoint = new ActionResultFilterEndPoint();
            actionResultFilters.Add(actionResultFilterEndPoint);

            var actionResultPipeline = new FilterPipelineBuilder<ActionResultFilterContext>(actionResultFilters, actionResultFilterContext);

            await actionResultPipeline.InvokeAsync();
        }

        private async Task<IDictionary<string, object>> GetParameterValues(ModelStateDictionary modelState)
        {
            var actionBindingContext = await _bindingProvider.GetActionBindingContextAsync(_actionContext);
            var parameters = _descriptor.Parameters;

            var parameterValues = new Dictionary<string, object>(parameters.Count, StringComparer.Ordinal);
            for (int i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                if (parameter.BodyParameterInfo != null)
                {
                    var inputFormatterContext = actionBindingContext.CreateInputFormatterContext(
                                                        modelState,
                                                        parameter);
                    await actionBindingContext.InputFormatter.ReadAsync(inputFormatterContext);
                    parameterValues[parameter.Name] = inputFormatterContext.Model;
                }
                else
                {
                    var modelBindingContext = actionBindingContext.CreateModelBindingContext(
                                                        modelState,
                                                        parameter);
                    actionBindingContext.ModelBinder.BindModel(modelBindingContext);
                    parameterValues[parameter.Name] = modelBindingContext.Model;
                }

            }

            return parameterValues;
        }

        private object[] GetArgumentValues(IDictionary<string, object> parameterValues)
        {
            var parameters = _descriptor.MethodInfo.GetParameters();
            var arguments = new object[parameters.Length];

            for (int i = 0; i < arguments.Length; i++)
            {
                object value;
                if (parameterValues.TryGetValue(parameters[i].Name, out value))
                {
                    arguments[i] = value;
                }
                else
                {
                    arguments[i] = parameters[i].DefaultValue;
                }
            }

            return arguments;
        }
    }
}