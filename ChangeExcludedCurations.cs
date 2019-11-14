using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Runtime.Serialization;
using AmblOn.State.API.Users.Models;
using AmblOn.State.API.Users.Harness;

namespace AmblOn.State.API.Users
{
    [DataContract]
    public class ChangeExcludedCurationsRequest
    {
        [DataMember]
        public virtual ExcludedCurations Curations { get; set; }
    }

    public static class ChangeExcludedCurations
    {
        [FunctionName("ChangeExcludedCurations")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            return await req.Manage<ChangeExcludedCurationsRequest, UsersState, UsersStateHarness>(log, async (mgr, reqData) =>
            {
                log.LogInformation($"Change visible curated locations: {reqData.Curations}");

                return await mgr.ChangeExcludedCurations(reqData.Curations);
            });
        }
    }
}