using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using AmblOn.State.API.Users.Models;
using AmblOn.State.API.Users.Harness;
using Microsoft.WindowsAzure.Storage;
using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AmblOn.State.API.Users
{
    [DataContract]
    public class EditItineraryActivityRequest
    {
        [DataMember]
        public virtual UserItineraryActivity ItineraryActivity { get; set; }

        [DataMember]
        public virtual Guid ItineraryID { get; set; }
    }
    public static class EditItineraryActivity
    {
        [FunctionName("EditItineraryActivity")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            return await req.Manage<EditItineraryActivityRequest, UsersState, UsersStateHarness>(log, async (mgr, reqData) =>
            {
                log.LogInformation($"Editing Itinerary Activity: {reqData.ItineraryID}");

                await mgr.EditItineraryActivity(reqData.ItineraryActivity, reqData.ItineraryID);

                return await mgr.WhenAll(
                );
            });
        }
    }
}
