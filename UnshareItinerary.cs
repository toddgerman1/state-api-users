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
    public class UnshareItineraryRequest
    {
        [DataMember]
        public virtual List<Itinerary> Itineraries { get; set; }

        [DataMember]
        public virtual List<string> Usernames {get; set;}
    }
    public static class UnshareItinerary
    {
        [FunctionName("UnshareItinerary")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            return await req.Manage<UnshareItineraryRequest, UsersState, UsersStateHarness>(log, async (mgr, reqData) =>
            {
                log.LogInformation($"Unsharing Itinerary");

                await mgr.UnshareItinerary(reqData.Itineraries, reqData.Usernames);

                return await mgr.WhenAll(
                );
            });
        }
    }
}
