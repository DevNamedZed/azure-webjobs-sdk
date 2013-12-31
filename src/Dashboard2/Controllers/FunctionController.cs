﻿using System;
using System.Linq;
using System.Web.Mvc;
using Dashboard.ViewModels;
using Microsoft.WindowsAzure.Jobs;
using FunctionInstanceStatus = Dashboard.ViewModels.FunctionInstanceStatus;

namespace Dashboard.Controllers
{
    public class FunctionController : Controller
    {
        private readonly Services _services;
        private readonly IFunctionTableLookup _functionTableLookup;
        private readonly IFunctionInstanceLookup _functionInstanceLookup;

        private const int MaxPageSize = 50;
        private const int DefaultPageSize = 10;

        internal FunctionController(
            Services services, 
            IFunctionTableLookup functionTableLookup, 
            IFunctionInstanceLookup functionInstanceLookup)
        {
            _services = services;
            _functionTableLookup = functionTableLookup;
            _functionInstanceLookup = functionInstanceLookup;
        }

        public ActionResult PartialInvocationLog()
        {
            var logger = _services.GetFunctionInstanceQuery();

            var query = new FunctionInstanceQueryFilter();
            var model = logger
                .GetRecent(10, query)
                .Select(x => new InvocationLogViewModel(x))
                .ToArray();

            return PartialView(model);
        }

        public ActionResult FunctionInstances(string functionName, bool? success, int? page, int? pageSize)
        {
            if (String.IsNullOrWhiteSpace(functionName))
            {
                return HttpNotFound();
            }

            FunctionDefinition func = _functionTableLookup.Lookup(functionName);

            if (func == null)
            {
                return HttpNotFound();
            }

            // ensure PageSize is not too big, and define a default value if not provided
            pageSize = pageSize.HasValue ? Math.Min(MaxPageSize, pageSize.Value) : DefaultPageSize;
            pageSize = Math.Max(0, pageSize.Value);

            page = page.HasValue ? page : 1;
            
            var skip = ((page - 1)*pageSize.Value).Value;

            // Do some analysis to find inputs, outputs, 
            var model = new FunctionInstancesViewModel
            {
                FunctionName = functionName,
                Success = success,
                Page = page,
                PageSize = pageSize
            };

            var query = new FunctionInstanceQueryFilter
            {
                Location = func.Location,
                Succeeded = success
            };

            var logger = _services.GetFunctionInstanceQuery();
            model.InvocationLogViewModels = logger
                .GetRecent(pageSize.Value, skip, query)
                .Select(e => new InvocationLogViewModel(e))
                .ToArray();

            return View(model);
        }

        public ActionResult FunctionInstance(string id)
        {
            if (String.IsNullOrWhiteSpace(id))
            {
                return HttpNotFound();
            }

            Guid guid;
            if (!Guid.TryParse(id, out guid))
            {
                return HttpNotFound();
            }

            var func = _functionInstanceLookup.Lookup(guid);

            if (func == null)
            {
                return HttpNotFound();
            }

            var functionModel = _functionTableLookup.Lookup(func.FunctionInstance.Location.GetId());
            if (functionModel == null)
            {
                string msg = string.Format("Function {0} has been unloaded from the server. Can't get log information", func.FunctionInstance.Location.GetId());
                return HttpNotFound(msg);
            }

            var model = new FunctionInstanceDetailsViewModel();
            model.InvocationLogViewModel = new InvocationLogViewModel(func);
            model.TriggerReason = new TriggerReasonViewModel(func.FunctionInstance.TriggerReason);
            model.IsAborted = model.InvocationLogViewModel.Status == FunctionInstanceStatus.Running && _services.IsDeleteRequested(func.FunctionInstance.Id);

            // Do some analysis to find inputs, outputs, 

            var descriptor = new FunctionDefinitionViewModel(functionModel);


            // Parallel arrays of static descriptor and actual instance info 
            ParameterRuntimeBinding[] args = func.FunctionInstance.Args;

            model.Parameters = LogAnalysis.GetParamInfo(descriptor.UnderlyingObject);
            LogAnalysis.ApplyRuntimeInfo(args, model.Parameters);
            LogAnalysis.ApplySelfWatchInfo(func.FunctionInstance, model.Parameters);


            ICausalityReader causalityReader = _services.GetCausalityReader();

            // fetch direct children
            model.Children = causalityReader
                .GetChildren(func.FunctionInstance.Id)
                .Select(r => new InvocationLogViewModel(_functionInstanceLookup.Lookup(r.ChildGuid))).ToArray();

            // fetch ancestor
            var parentGuid = func.FunctionInstance.TriggerReason.ParentGuid;
            if (parentGuid != Guid.Empty)
            {
                model.Ancestor = new InvocationLogViewModel(_functionInstanceLookup.Lookup(parentGuid));
            }

            return View("FunctionInstance", model);
        }

        [HttpPost]
        public ActionResult Abort(string id)
        {
            if (String.IsNullOrWhiteSpace(id))
            {
                return HttpNotFound();
            }

            Guid guid;
            if (!Guid.TryParse(id, out guid))
            {
                return HttpNotFound();
            }

            var func = _functionInstanceLookup.Lookup(guid);

            if (func == null)
            {
                return HttpNotFound();
            }

            _services.PostDeleteRequest(func.FunctionInstance);

            return RedirectToAction("FunctionInstance", new { id });
        }
    }
}