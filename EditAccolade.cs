﻿using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using AmblOn.State.API.Users.Models;
using Fathym;using Microsoft.Azure.WebJobs.Extensions.SignalRService;using AmblOn.State.API.Users.State;using Microsoft.WindowsAzure.Storage.Blob;using LCU.StateAPI.Utilities;
using Microsoft.WindowsAzure.Storage;
using System.Runtime.Serialization;
using System.Collections.Generic;
using System.Drawing;
using LCU.Presentation;
using AmblOn.State.API.Users.Graphs;

namespace AmblOn.State.API.Users
{
    [DataContract]
    public class EditAccoladeRequest
    {
        [DataMember]
        public virtual UserAccolade Accolade { get; set; }

        [DataMember]
        public virtual Guid LocationID { get; set; }
    }

    public class EditAccolade
    {
        #region Fields
        protected AmblOnGraph amblGraph;
        #endregion

        #region Constructors
        public EditAccolade(AmblOnGraph amblGraph)
        {
            this.amblGraph = amblGraph;
        }
        #endregion

        [FunctionName("EditAccolade")]
        public virtual async Task<Status> Run([HttpTrigger(AuthorizationLevel.Admin)] HttpRequest req, ILogger log,
            [SignalR(HubName = UsersState.HUB_NAME)]IAsyncCollector<SignalRMessage> signalRMessages,
            [Blob("state-api/{headers.lcu-ent-api-key}/{headers.lcu-hub-name}/{headers.x-ms-client-principal-id}/{headers.lcu-state-key}", FileAccess.ReadWrite)] CloudBlockBlob stateBlob)
        {
            return await stateBlob.WithStateHarness<UsersState, AddAccoladeRequest, UsersStateHarness>(req, signalRMessages, log,
                async (harness, reqData, actReq) =>
            {
                log.LogInformation($"EditAccolade");

                var stateDetails = StateUtils.LoadStateDetails(req);

                await harness.EditAccolade(amblGraph, stateDetails.Username, stateDetails.EnterpriseAPIKey, reqData.Accolade, reqData.LocationID);

                return Status.Success;
            });
        }
    }
}
