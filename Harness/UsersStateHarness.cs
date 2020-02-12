using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AmblOn.State.API.Users.Graphs;
using AmblOn.State.API.Users.Models;
using Fathym;
using Fathym.API;
using Fathym.Design.Singleton;
using LCU.Graphs;
using LCU.Graphs.Registry.Enterprises;
using LCU.StateAPI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Device.Location;
using LCU.Presentation;
using Microsoft.AspNetCore.WebUtilities;
using LCU.Personas.Client.Applications;
using Microsoft.AspNetCore.Http.Internal;
using System.IO;
using LCU.Personas.Client.Enterprises;
using LCU;

namespace AmblOn.State.API.Users.Harness
{
    public class UsersStateHarness : LCUStateHarness<UsersState>
    {
        #region Fields
        protected readonly AmblOnGraph amblGraph;

        protected readonly ApplicationManagerClient appMgr;

        protected readonly Guid enterpriseId;

        protected readonly EnterpriseManagerClient entMgr;

        #endregion

        #region Properties
        #endregion

        #region Constructors
        public UsersStateHarness(HttpRequest req, ILogger log, UsersState state)
            : base(req, log, state)
        {
            // TODO: This needs to be injected , registered at startup as a singleton
            amblGraph = new AmblOnGraph(new GremlinClientPoolManager(
                new ApplicationProfileManager(
                    Environment.GetEnvironmentVariable("LCU-DATABASE-CLIENT-POOL-SIZE").As<int>(4),
                    Environment.GetEnvironmentVariable("LCU-DATABASE-CLIENT-MAX-POOL-CONNS").As<int>(32),
                    Environment.GetEnvironmentVariable("LCU-DATABASE-CLIENT-TTL").As<int>(60)
                ),
                new LCUGraphConfig()
                {
                    APIKey = Environment.GetEnvironmentVariable("LCU-GRAPH-API-KEY"),
                    Database = Environment.GetEnvironmentVariable("LCU-GRAPH-DATABASE"),
                    Graph = Environment.GetEnvironmentVariable("LCU-GRAPH"),
                    Host = Environment.GetEnvironmentVariable("LCU-GRAPH-HOST")
                })
            );

            appMgr = req.ResolveClient<ApplicationManagerClient>(logger);

            entMgr = req.ResolveClient<EnterpriseManagerClient>(logger);

            var enterprise = entMgr.GetEnterprise(details.EnterpriseAPIKey).GetAwaiter().GetResult();

            enterpriseId = enterprise.Model.ID;

            appMgr.RegisterApplicationProfile(details.ApplicationID, new LCU.ApplicationProfile()
            {
                DatabaseClientMaxPoolConnections = Environment.GetEnvironmentVariable("LCU-DATABASE-CLIENT-MAX-POOL-CONNS").As<int>(32),
                DatabaseClientPoolSize = Environment.GetEnvironmentVariable("LCU-DATABASE-CLIENT-POOL-SIZE").As<int>(4),
                DatabaseClientTTLMinutes = Environment.GetEnvironmentVariable("LCU-DATABASE-CLIENT-TTL").As<int>(60)
            });
        }
        #endregion

        #region API Methods
        #region Add
        public virtual async Task<UsersState> AddAccolade(UserAccolade accolade, Guid locationId)
        {
            ensureStateObject();

            var accoladeResp = await amblGraph.AddAccolade(details.Username, details.EnterpriseAPIKey, accolade, locationId);

            if (accoladeResp.Status)
            {
                accolade.ID = accoladeResp.Model;

                if (!state.UserAccolades.Any(x => x.ID == accolade.ID))
                    state.UserAccolades.Add(accolade);
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> AddAlbum(UserAlbum album, List<ImageMessage> images)
        {
            ensureStateObject();

            album.Photos = mapImageDataToUserPhotos(album.Photos, images);

            var albumResp = await amblGraph.AddAlbum(details.Username, details.EnterpriseAPIKey, album);

            if (albumResp.Status)
            {
                album.ID = albumResp.Model;

                if (!state.UserAlbums.Any(x => x.ID == album.ID))
                    state.UserAlbums.Add(album);

                if (album.Photos.Count > 0)
                {
                    album.Photos.ForEach(
                        (photo) =>
                        {
                            AddPhoto(photo, album.ID.HasValue ? album.ID.Value : Guid.Empty, photo.LocationID.HasValue ? photo.LocationID.Value : Guid.Empty).GetAwaiter().GetResult();
                        });
                }
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> AddItinerary(Itinerary itinerary)
        {
            ensureStateObject();

            itinerary.CreatedDateTime = DateTime.Now;

            var itineraryResp = await amblGraph.AddItinerary(details.Username, details.EnterpriseAPIKey, itinerary);

            if (itineraryResp.Status)
            {
                itinerary.ID = itineraryResp.Model;

                itinerary.ActivityGroups.ForEach(
                    (activityGroup) =>
                    {
                        var activityGroupResp = amblGraph.AddActivityGroup(details.Username, details.EnterpriseAPIKey, itinerary.ID, activityGroup).GetAwaiter().GetResult();

                        if (activityGroupResp.Status)
                        {
                            activityGroup.ID = activityGroupResp.Model;

                            activityGroup.Activities.ForEach(
                                (activity) =>
                                {
                                    var activityResp = amblGraph.AddActivity(details.Username, details.EnterpriseAPIKey, itinerary.ID, activityGroup.ID, activity).GetAwaiter().GetResult();

                                    if (activityResp.Status)
                                    {
                                        activity.ID = activityResp.Model;
                                    }
                                });
                        }
                    });

                if (!state.UserItineraries.Any(x => x.ID == itinerary.ID))
                    state.UserItineraries.Add(itinerary);
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> AddLocation(UserLocation location)
        {
            ensureStateObject();

            if (state.UserLayers.Any(x => x.ID == location.LayerID && !x.Shared))
            {
                var locationResp = await amblGraph.AddLocation(details.Username, details.EnterpriseAPIKey, location);

                if (locationResp.Status)
                {
                    location.ID = locationResp.Model;

                    if (state.SelectedUserLayerIDs.Contains(location.LayerID))
                    {
                        state.VisibleUserLocations.Add(location);
                        state.AllUserLocations.Add(location);

                        var userMap = state.UserMaps.FirstOrDefault(x => x.ID == state.SelectedUserMapID);

                        if (userMap != null)
                            state.VisibleUserLocations = limitUserLocationsGeographically(state.VisibleUserLocations, userMap.Coordinates);

                        state.VisibleUserLocations = state.VisibleUserLocations.Distinct().ToList();
                    }
                }
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> AddMap(UserMap map)
        {
            ensureStateObject();

            BaseResponse<Guid> mapResp = new BaseResponse<Guid>() { Status = Status.Initialized };

            if (!map.Shared)
                mapResp = await amblGraph.AddMap(details.Username, details.EnterpriseAPIKey, map);
            else
                mapResp = await amblGraph.AddSharedMap(details.Username, details.EnterpriseAPIKey, map, (map.InheritedID.HasValue ? map.InheritedID.Value : Guid.Empty));

            if (mapResp.Status)
            {
                map.ID = mapResp.Model;

                state.UserMaps.Add(map);

                state.UserMaps = state.UserMaps.Distinct().ToList();

                state.SelectedUserMapID = map.ID.Value;
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> AddPhoto(UserPhoto photo, Guid albumID, Guid locationID)
        {
            ensureStateObject();

            await appMgr.SaveFile(photo.ImageData.Data, enterpriseId, "admin/" + details.Username + "/albums/" + albumID.ToString(), QueryHelpers.ParseQuery(photo.ImageData.Headers)["filename"], new Guid(details.ApplicationID), "/");

            photo.URL = "/admin/" + details.Username + "/albums/" + albumID.ToString() + "/" + QueryHelpers.ParseQuery(photo.ImageData.Headers)["filename"];

            photo.ImageData = null;

            var photoResp = await amblGraph.AddPhoto(details.Username, details.EnterpriseAPIKey, photo, albumID, locationID);

            if (photoResp.Status)
            {
                photo.ID = photoResp.Model;
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> AddSelectedLayer(Guid layerID)
        {
            ensureStateObject();

            if (state.UserLayers.Any(x => x.ID == layerID))
                state.SelectedUserLayerIDs.Add(layerID);

            //TODO: Check for whether locations are in AllLocations
            var locationsToAdd = await fetchVisibleUserLocations(details.Username, details.EnterpriseAPIKey, new List<Guid>() { layerID });

            state.AllUserLocations.AddRange(locationsToAdd);

            state.AllUserLocations = state.AllUserLocations.Distinct().ToList();

            var userMap = state.UserMaps.FirstOrDefault(x => x.ID == state.SelectedUserMapID);

            if (userMap != null)
            {
                state.VisibleUserLocations.AddRange(limitUserLocationsGeographically(locationsToAdd, userMap.Coordinates));

                state.VisibleUserLocations = state.VisibleUserLocations.Distinct().ToList();
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> AddTopList(UserTopList topList)
        {
            ensureStateObject();

            var topListResp = await amblGraph.AddTopList(details.Username, details.EnterpriseAPIKey, topList);

            if (topListResp.Status)
            {
                topList.ID = topListResp.Model;

                if (!state.UserTopLists.Any(x => x.ID == topList.ID))
                    state.UserTopLists.Add(topList);
            }

            state.Loading = false;

            return state;
        }
        #endregion
        public virtual async Task<UsersState> ChangeViewingArea(float[] coordinates)
        {
            ensureStateObject();

            var userMap = state.UserMaps.FirstOrDefault(x => x.ID == state.SelectedUserMapID);

            if (userMap != null)
            {
                userMap.Coordinates = coordinates;
                
                //TODO : Does this need to be reloaded
                //var visibleLocations = await fetchVisibleUserLocations(details.Username, details.EnterpriseAPIKey, state.SelectedUserLayerIDs);

                state.VisibleUserLocations = limitUserLocationsGeographically(state.AllUserLocations, userMap.Coordinates)
                                            .Distinct()
                                            .ToList();

                //state.VisibleUserLocations = state.VisibleUserLocations.Distinct().ToList();
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> ChangeExcludedCurations(ExcludedCurations curations)
        {
            ensureStateObject();

            state.ExcludedCuratedLocations = curations;

            await amblGraph.EditExcludedCurations(details.Username, details.EnterpriseAPIKey, curations);

            return state;
        }

        #region Delete
        public virtual async Task<UsersState> DeleteAccolades(Guid[] accoladeIDs, Guid locationId)
        {
            ensureStateObject();

            var accoladeResp = await amblGraph.DeleteAccolades(details.Username, details.EnterpriseAPIKey, accoladeIDs, locationId);

            if (accoladeResp.Status)
            {
                state.UserAccolades.RemoveAll(x => accoladeIDs.ToList<Guid>().Contains(x.ID ?? Guid.Empty));

                state.UserAccolades = state.UserAccolades.Distinct().ToList();
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> DeleteAlbum(Guid albumID)
        {
            ensureStateObject();

            var albumResp = await amblGraph.DeleteAlbum(details.Username, details.EnterpriseAPIKey, albumID);

            if (albumResp.Status)
            {
                var existing = state.UserAlbums.FirstOrDefault(x => x.ID == albumID);

                if (existing != null)
                    state.UserAlbums.Remove(existing);

                state.UserAlbums = state.UserAlbums.Distinct().ToList();
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> DeleteItinerary(Guid itineraryID)
        {
            ensureStateObject();

            var itinerary = state.UserItineraries.FirstOrDefault(x => x.ID == itineraryID);

            if (itinerary != null)
            {
                var success = true;

                itinerary.ActivityGroups.ForEach(
                    (activityGroup) =>
                    {
                        activityGroup.Activities.ForEach(
                            (activity) =>
                            {
                                var actResp = amblGraph.DeleteActivity(details.Username, details.EnterpriseAPIKey, itinerary.ID, activityGroup.ID, activity.ID).GetAwaiter().GetResult();

                                if (!actResp.Status)
                                    success = false;
                            });

                        if (success)
                        {
                            var actGroupResp = amblGraph.DeleteActivityGroup(details.Username, details.EnterpriseAPIKey, itinerary.ID, activityGroup.ID).GetAwaiter().GetResult();

                            if (!actGroupResp.Status)
                                success = false;
                        }
                    });

                if (success)
                {
                    var itineraryResp = await amblGraph.DeleteItinerary(details.Username, details.EnterpriseAPIKey, itineraryID);

                    if (!itineraryResp.Status)
                        success = false;
                }

                if (success)
                {
                    var existing = state.UserItineraries.FirstOrDefault(x => x.ID == itineraryID);

                    if (existing != null)
                        state.UserItineraries.Remove(existing);

                    state.UserItineraries = state.UserItineraries.Distinct().ToList();
                }
                else
                    state.Error = "Error deleting itinerary.";
            }
            else
                state.Error = "Itinerary not found.";

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> DeleteLocation(Guid locationID)
        {
            ensureStateObject();

            var locationResp = await amblGraph.DeleteLocation(details.Username, details.EnterpriseAPIKey, locationID);

            if (locationResp.Status)
            {
                var existingVisible = state.VisibleUserLocations.FirstOrDefault(x => x.ID == locationID);

                if (existingVisible != null) {
                    state.VisibleUserLocations.Remove(existingVisible);
                    state.AllUserLocations.RemoveAll(item => item.ID == existingVisible.ID);
                }

                state.VisibleUserLocations = state.VisibleUserLocations.Distinct().ToList();
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> DeleteMap(Guid mapID)
        {
            ensureStateObject();

            var userMap = state.UserMaps.FirstOrDefault(x => x.ID == mapID);

            if (userMap != null && userMap.Deletable)
            {
                BaseResponse mapResp = new BaseResponse() { Status = Status.Initialized };

                if (!userMap.Shared)
                    mapResp = await amblGraph.DeleteMap(details.Username, details.EnterpriseAPIKey, mapID);
                else
                    mapResp = await amblGraph.DeleteSharedMap(details.Username, details.EnterpriseAPIKey, mapID);

                if (mapResp.Status)
                {
                    var existingMap = state.UserMaps.FirstOrDefault(x => x.ID == mapID);

                    if (existingMap != null)
                        state.UserMaps.Remove(existingMap);

                    state.UserMaps = state.UserMaps.Distinct().ToList();

                    if (!state.UserMaps.Any(x => x.Primary == true))
                    {
                        var newPrimary = state.UserMaps.FirstOrDefault(x => x.Shared && !x.Deletable);

                        if (newPrimary != null)
                            newPrimary.Primary = true;
                        else if (state.UserMaps.Count > 0)
                            state.UserMaps.First().Primary = true;
                    }

                    if (state.UserMaps.Any(x => x.Primary))
                    {
                        var primaryMap = state.UserMaps.First(x => x.Primary);

                        state.SelectedUserMapID = (primaryMap.ID.HasValue ? primaryMap.ID.Value : Guid.Empty);

                        // TODO:  Should layers and locations be reloaded, or loaded from local collection
                        state.SelectedUserLayerIDs.Clear();

                        state.SelectedUserLayerIDs.Add(primaryMap.DefaultLayerID);

                        //var visibleLocations = await fetchVisibleUserLocations(details.Username, details.EnterpriseAPIKey, state.SelectedUserLayerIDs);
                        
                        state.VisibleUserLocations = limitUserLocationsGeographically(state.AllUserLocations, primaryMap.Coordinates);

                        state.VisibleUserLocations = state.VisibleUserLocations.Distinct().ToList();
                    }
                }
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> DeleteMaps(Guid[] mapIDs)
        {
            ensureStateObject();

            var mapResp = await amblGraph.DeleteMaps(details.Username, details.EnterpriseAPIKey, mapIDs);

            if (mapResp.Status)
            {
                state.UserMaps.RemoveAll(x => mapIDs.ToList().Contains(x.ID ?? default(Guid)));

                state.UserMaps = state.UserMaps.Distinct().ToList();

                if (!state.UserMaps.Any(x => x.Primary == true))
                {
                    var newPrimary = state.UserMaps.FirstOrDefault(x => x.Shared && !x.Deletable);

                    if (newPrimary != null)
                        newPrimary.Primary = true;
                    else if (state.UserMaps.Count > 0)
                        state.UserMaps.First().Primary = true;
                }
                


                if (state.UserMaps.Any(x => x.Primary))
                {
                    var primaryMap = state.UserMaps.First(x => x.Primary);

                    state.SelectedUserMapID = (primaryMap.ID.HasValue ? primaryMap.ID.Value : Guid.Empty);

                    // TODO: Do layers need to be reloaded if a map is removed
                    state.SelectedUserLayerIDs.Clear();

                    state.SelectedUserLayerIDs.Add(primaryMap.DefaultLayerID);

                    // TODO: Reload from a local collection instead
                    //var visibleLocations = await fetchVisibleUserLocations(details.Username, details.EnterpriseAPIKey, state.SelectedUserLayerIDs);

                    state.VisibleUserLocations = limitUserLocationsGeographically(state.AllUserLocations, primaryMap.Coordinates)
                                                .Distinct()
                                                .ToList();

                    //state.VisibleUserLocations = state.VisibleUserLocations.Distinct().ToList();
                }
            }


            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> DeletePhoto(Guid photoID)
        {
            ensureStateObject();

            var photoResp = await amblGraph.DeletePhoto(details.Username, details.EnterpriseAPIKey, photoID);

            if (photoResp.Status)
            {
                var existingAlbum = state.UserAlbums.FirstOrDefault(x => x.Photos.Any(y => y.ID == photoID));

                if (existingAlbum != null)
                {
                    var existingPhoto = existingAlbum.Photos.FirstOrDefault(x => x.ID == photoID);

                    if (existingPhoto != null)
                    {
                        existingAlbum.Photos.Remove(existingPhoto);

                        existingAlbum.Photos = existingAlbum.Photos.Distinct().ToList();
                    }
                }
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> DeleteTopList(Guid topListID)
        {
            ensureStateObject();

            var topListResp = await amblGraph.DeleteTopList(details.Username, details.EnterpriseAPIKey, topListID);

            if (topListResp.Status)
            {
                var existing = state.UserTopLists.FirstOrDefault(x => x.ID == topListID);

                if (existing != null)
                    state.UserTopLists.Remove(existing);

                state.UserTopLists = state.UserTopLists.Distinct().ToList();
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> DedupLocationsByMap(Guid mapID)
        {
            ensureStateObject();

            var locationResp = await amblGraph.DedupLocationsByMap(details.Username, details.EnterpriseAPIKey, mapID);
            
            // Do not refresh state for now

        
            state.Loading = false;

            return state;
        }
        #endregion

        #region Edit
        public virtual async Task<UsersState> EditAccolade(UserAccolade accolade, Guid locationId)
        {
            ensureStateObject();

            var existing = state.UserAccolades.FirstOrDefault(x => x.ID == accolade.ID);

            if (existing != null)
            {
                var accoladeResp = await amblGraph.EditAccolade(details.Username, details.EnterpriseAPIKey, accolade, locationId);

                if (accoladeResp.Status)
                {

                    state.UserAccolades.Remove(existing);

                    state.UserAccolades.Add(accolade);

                    state.UserAccolades = state.UserAccolades.Distinct().ToList();
                }
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> EditAlbum(UserAlbum album)
        {
            ensureStateObject();

            var existing = state.UserAlbums.FirstOrDefault(x => x.ID == album.ID);

            if (existing != null)
            {
                var albumResp = await amblGraph.EditAlbum(details.Username, details.EnterpriseAPIKey, album);

                if (albumResp.Status)
                {
                    state.UserAlbums.Remove(existing);

                    state.UserAlbums.Add(album);

                    state.UserAlbums = state.UserAlbums.Distinct().ToList();
                }
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> EditItinerary(Itinerary itinerary)
        {
            ensureStateObject();

            var existing = state.UserItineraries.FirstOrDefault(x => x.ID == itinerary.ID);

            if (existing != null)
            {
                if (existing.Editable)
                {
                    var success = true;

                    var itineraryResp = await amblGraph.EditItinerary(details.Username, details.EnterpriseAPIKey, itinerary);

                        if (!itineraryResp.Status)
                            success = false;

                    if (success)
                    {
                        itinerary.ActivityGroups.ForEach(
                            (activityGroup) =>
                            {
                                var agExisting = existing.ActivityGroups.FirstOrDefault(x => x.ID == activityGroup.ID);

                                if (agExisting == null)
                                {
                                    var addActGResp = amblGraph.AddActivityGroup(details.Username, details.EnterpriseAPIKey, itinerary.ID, activityGroup).GetAwaiter().GetResult();

                                    if (addActGResp.Status)
                                    {
                                        activityGroup.ID = addActGResp.Model;

                                        activityGroup.Activities.ForEach(
                                            (activity) =>
                                            {
                                                var addActResp = amblGraph.AddActivity(details.Username, details.EnterpriseAPIKey, itinerary.ID, activityGroup.ID, activity).GetAwaiter().GetResult();

                                                activity.ID = addActResp.Model;

                                                if (!addActResp.Status)
                                                    success = false;
                                            });
                                    }
                                    else
                                        success = false;
                                }
                                else
                                {
                                    activityGroup.Activities.ForEach(
                                        (activity) =>
                                        {
                                            var aExisting = agExisting.Activities.FirstOrDefault(x => x.ID == activity.ID);

                                            if (aExisting == null)
                                            {
                                                var addActResp = amblGraph.AddActivity(details.Username, details.EnterpriseAPIKey, itinerary.ID, activityGroup.ID, activity).GetAwaiter().GetResult();

                                                activity.ID = addActResp.Model;

                                                if (!addActResp.Status)
                                                    success = false;
                                            }
                                            else
                                            {
                                                var editActResp = amblGraph.EditActivity(details.Username, details.EnterpriseAPIKey, activity).GetAwaiter().GetResult();

                                                if (!editActResp.Status)
                                                    success = false;
                                            }
                                        });

                                    var editActGResp = amblGraph.EditActivityGroup(details.Username, details.EnterpriseAPIKey, activityGroup).GetAwaiter().GetResult();

                                    if (!editActGResp.Status)
                                        success = false;
                                }
                            });

                        existing.ActivityGroups.ForEach(
                            (activityGroup) =>
                        {
                            var agNew = itinerary.ActivityGroups.FirstOrDefault(x => x.ID == activityGroup.ID);

                            if (agNew == null)
                            {
                                activityGroup.Activities.ForEach(
                                    (activity) =>
                                    {
                                        var delActResp = amblGraph.DeleteActivity(details.Username, details.EnterpriseAPIKey, itinerary.ID, activityGroup.ID, activity.ID).GetAwaiter().GetResult();

                                        if (!delActResp.Status)
                                            success = false;
                                    });
                                
                                if (success)
                                {
                                    var delActGResp = amblGraph.DeleteActivityGroup(details.Username, details.EnterpriseAPIKey, itinerary.ID, activityGroup.ID).GetAwaiter().GetResult();

                                    if (!delActGResp.Status)
                                        success = false;
                                }
                            }
                            else
                            {
                                activityGroup.Activities.ForEach(
                                    (activity) =>
                                    {
                                        var aNew = agNew.Activities.FirstOrDefault(x => x.ID == activity.ID);

                                        if (aNew == null)
                                        {
                                            var delActResp = amblGraph.DeleteActivity(details.Username, details.EnterpriseAPIKey, itinerary.ID, activityGroup.ID, activity.ID).GetAwaiter().GetResult();

                                            if (!delActResp.Status)
                                                success = false;
                                        }
                                    });
                            }
                        });
                    }

                    if (success)
                        state.UserItineraries = fetchUserItineraries(details.Username, details.EnterpriseAPIKey).GetAwaiter().GetResult();
                    else
                        state.Error = "General Error updating user itinerary.";
                }
                    else state.Error = "Cannot edit a shared itinerary.";

            }
            else
                state.Error = "Itinerary not found.";

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> EditLocation(UserLocation location)
        {
            ensureStateObject();

            if (state.UserLayers.Any(x => x.ID == location.LayerID && !x.Shared))
            {
                var locationResp = await amblGraph.EditLocation(details.Username, details.EnterpriseAPIKey, location);

                if (locationResp.Status)
                {
                    if (state.SelectedUserLayerIDs.Contains(location.LayerID))
                    {
                        var existingVisible = state.VisibleUserLocations.FirstOrDefault(x => x.ID == location.ID);

                        if (existingVisible != null) {
                            state.VisibleUserLocations.Remove(existingVisible);
                            state.AllUserLocations.RemoveAll(item => item.ID == existingVisible.ID);
                        }

                        state.VisibleUserLocations.Add(location);
                        state.AllUserLocations.Add(location);

                        var userMap = state.UserMaps.FirstOrDefault(x => x.ID == state.SelectedUserMapID);

                        if (userMap != null)
                            state.VisibleUserLocations = limitUserLocationsGeographically(state.VisibleUserLocations, userMap.Coordinates);

                        state.VisibleUserLocations = state.VisibleUserLocations.Distinct().ToList();
                    }
                }
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> EditMap(UserMap map)
        {
            ensureStateObject();

            var userMap = state.UserMaps.FirstOrDefault(x => x.ID == map.ID);

            BaseResponse mapResp = new BaseResponse() { Status = Status.Initialized };

            if (userMap != null && !userMap.Shared)
                mapResp = await amblGraph.EditMap(details.Username, details.EnterpriseAPIKey, map);
            else if (userMap != null)
                mapResp = await amblGraph.EditSharedMap(details.Username, details.EnterpriseAPIKey, map);

            if (mapResp.Status)
            {
                var existingMap = state.UserMaps.FirstOrDefault(x => x.ID == map.ID);

                if (existingMap != null)
                {
                    state.UserMaps.Remove(existingMap);

                    state.UserMaps.Add(map);

                    state.UserMaps = state.UserMaps.Distinct().ToList();
                }
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> EditPhoto(UserPhoto photo, Guid albumID)
        {
            ensureStateObject();

            var existingAlbum = state.UserAlbums.FirstOrDefault(x => x.Photos.Any(y => y.ID == photo.ID));

            if (existingAlbum != null)
            {
                var existingPhoto = existingAlbum.Photos.FirstOrDefault(x => x.ID == photo.ID);

                if (existingPhoto != null)
                {
                    var photoResp = await amblGraph.EditPhoto(details.Username, details.EnterpriseAPIKey, photo, albumID);

                    if (photoResp.Status)
                    {
                        existingAlbum.Photos.Remove(existingPhoto);

                        existingAlbum.Photos.Add(photo);

                        existingAlbum.Photos = existingAlbum.Photos.Distinct().ToList();
                    }
                }
            }

            state.Loading = false;

            return state;
        }
        public virtual async Task<UsersState> EditTopList(UserTopList topList)
        {
            ensureStateObject();

            var existing = state.UserTopLists.FirstOrDefault(x => x.ID == topList.ID);

            if (existing != null)
            {
                var topListResp = await amblGraph.EditTopList(details.Username, details.EnterpriseAPIKey, topList);

                if (topListResp.Status)
                {

                    state.UserTopLists.Remove(existing);

                    state.UserTopLists.Add(topList);

                    state.UserTopLists = state.UserTopLists.Distinct().ToList();
                }
            }

            state.Loading = false;

            return state;
        }
        #endregion

        public virtual async Task<UsersState> Ensure()
        {
            ensureStateObject();

            state.UserAlbums = await fetchUserAlbums(details.Username, details.EnterpriseAPIKey);

            state.UserItineraries = await fetchUserItineraries(details.Username, details.EnterpriseAPIKey);

            state.UserLayers = await fetchUserLayers(details.Username, details.EnterpriseAPIKey);

            state.UserMaps = await fetchUserMaps(details.Username, details.EnterpriseAPIKey);

            if (state.SelectedUserMapID.IsEmpty())
            {
                var primaryMap = state.UserMaps.FirstOrDefault(x => x.Primary == true);

                if (primaryMap != null)
                    state.SelectedUserMapID = (primaryMap.ID.HasValue ? primaryMap.ID.Value : Guid.Empty);
            }
            
            // Load only on initial state load
            if (state.AllUserLocations.Count==0) {
                state.AllUserLocations = await fetchVisibleUserLocations(details.Username, details.EnterpriseAPIKey, state.SelectedUserLayerIDs);
            }

            state.ExcludedCuratedLocations = await fetchUserExcludedCurations(details.Username, details.EnterpriseAPIKey);

            var userMap = state.UserMaps.FirstOrDefault(x => x.ID == state.SelectedUserMapID);

            if (userMap != null)
            {
                state.VisibleUserLocations = limitUserLocationsGeographically(state.AllUserLocations, userMap.Coordinates)
                                                .Distinct()
                                                .ToList();
                //state.VisibleUserLocations = state.VisibleUserLocations.Distinct().ToList();
            }

            var userLayer = state.UserLayers.Where(x => x.Title == "User").FirstOrDefault();

            var userLayerID = (userLayer == null) ? Guid.Empty : userLayer.ID;

            state.UserTopLists = await fetchUserTopLists(details.Username, details.EnterpriseAPIKey, userLayerID);

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> GlobalSearch(string searchTerm)
        {
            ensureStateObject();
            
            var userMap = state.UserMaps.FirstOrDefault(x => x.ID == state.SelectedUserMapID);

            if (userMap != null)
            {
                var circle = computeCircle(userMap.Coordinates[0], userMap.Coordinates[1], userMap.Coordinates[2], userMap.Coordinates[3]);

                var searchLocations = limitUserLocationsBySearch(state.AllUserLocations, searchTerm);

                var radiusLocations = limitUserLocationsByRadius(searchLocations, circle.Item1, circle.Item2, circle.Item3);
                                                    
                state.LocalSearchUserLocations = radiusLocations
                        .Distinct()
                        .OrderBy(x => x.Title)
                        .ToList();

                var localIDs = state.LocalSearchUserLocations.Select(x => x.ID);

                state.OtherSearchUserLocations = searchLocations
                        .Where(x => !localIDs.Contains(x.ID))
                        .Distinct()
                        .OrderBy(x => x.Title)
                        .ToList();
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> Load()
        {
            ensureStateObject();

            state.UserAlbums = await fetchUserAlbums(details.Username, details.EnterpriseAPIKey);

            state.UserItineraries = await fetchUserItineraries(details.Username, details.EnterpriseAPIKey);

            state.UserLayers = await fetchUserLayers(details.Username, details.EnterpriseAPIKey);

            state.UserMaps = await fetchUserMaps(details.Username, details.EnterpriseAPIKey);

            var primaryMap = state.UserMaps.FirstOrDefault(x => x.Primary == true);

            if (primaryMap != null)
            {
                state.SelectedUserMapID = (primaryMap.ID.HasValue ? primaryMap.ID.Value : Guid.Empty);

                var userMap = state.UserMaps.FirstOrDefault(x => x.ID == state.SelectedUserMapID);

                if (userMap != null)
                {
                    state.SelectedUserLayerIDs.Clear();

                    var layerID = userMap.DefaultLayerID;

                    var layer = state.UserLayers.FirstOrDefault(x => x.ID == layerID);

                    if (layer == null)
                        layer = state.UserLayers.FirstOrDefault(x => x.InheritedID == layerID);

                    if (layer != null)
                        layerID = layer.ID;

                    state.SelectedUserLayerIDs.Add(layerID);

                    state.AllUserLocations = await fetchVisibleUserLocations(details.Username, details.EnterpriseAPIKey, state.SelectedUserLayerIDs);
                    
                    state.VisibleUserLocations = limitUserLocationsGeographically(state.AllUserLocations, userMap.Coordinates)
                                                    .Distinct()
                                                    .ToList();
                }
            }

            var userLayer = state.UserLayers.Where(x => x.Title == "User").FirstOrDefault();

            var userLayerID = (userLayer == null) ? Guid.Empty : userLayer.ID;

            state.UserTopLists = await fetchUserTopLists(details.Username, details.EnterpriseAPIKey, userLayerID);

            state.ExcludedCuratedLocations = await fetchUserExcludedCurations(details.Username, details.EnterpriseAPIKey);

            state.Loading = false;

            return state;
        }

        public virtual async Task LoadCuratedLocationsIntoDB(string ownerEmail, List<dynamic> list, List<string> acclist, Guid layerID)
        {

            float testFloat = 0;

            var workingList = list.Where(x => x.Latitude != null && float.TryParse(x.Latitude.ToString(), out testFloat)
                && x.Longitude != null && float.TryParse(x.Longitude.ToString(), out testFloat)).ToList();

            // Create location object
            workingList.ForEach(
           async (jsonLocation) =>
           {
               var location = new UserLocation()
               {
                   Address = jsonLocation.Address,
                   Country = jsonLocation.Country,
                   Icon = jsonLocation.Icon,
                   Instagram = jsonLocation.Instagram,
                   Latitude = jsonLocation.Latitude,
                   LayerID = layerID,
                   Longitude = jsonLocation.Longitude,
                   State = jsonLocation.State,
                   Telephone = jsonLocation.Telephone,
                   Title = jsonLocation.Title,
                   Town = jsonLocation.Town,
                   Website = jsonLocation.Website,
                   ZipCode = jsonLocation.Zipcode
               };
            
               // Extract all properties of jsonLocation
               JObject propetiesObj = jsonLocation;
               var jsonProperties = propetiesObj.ToObject<Dictionary<string, object>>();
    
               // Create location object if it doesn't already exist in the graph DB
               var resp = amblGraph.AddLocation(ownerEmail, details.EnterpriseAPIKey, location);

               if (resp.Result.Model != null) {
               // Iterate through accolade list 
               acclist.ForEach((accName) => {
                    // If it's in the JSON properties list for this location
                    var accKey = jsonProperties.Keys.FirstOrDefault(x => x == accName);
                  
                    if (!String.IsNullOrEmpty(accKey) && (!String.IsNullOrEmpty(jsonProperties[accKey].ToString()))) {
                        UserAccolade accolade;
                        
                        // Awkward logic to include support for Michelin stars
                        if (accKey == "Michelin")  {
                            accolade = new UserAccolade() {
                                Rank = jsonProperties[accKey].ToString(),
                                Title = accKey,
                                Year = jsonProperties["Mich Since"].ToString()
                            }; 
                        } else {                        
                            accolade = new UserAccolade() {
                                Rank = jsonProperties[accKey].ToString(),
                                Title = accKey
                            };                                                            
                        }
                        var accResp = amblGraph.AddAccolade(ownerEmail, details.EnterpriseAPIKey, accolade, resp.Result.Model);
                    }
                });
               }
           });
        }

        public virtual async Task<UsersState> RemoveSelectedLayer(Guid layerID)
        {
            ensureStateObject();

            state.SelectedUserLayerIDs.Remove(layerID);

            state.VisibleUserLocations = removeUserLocationsByLayerID(state.VisibleUserLocations, layerID);

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> SendInvite(string email)
        {
            ensureStateObject();

            var subject = "";
            var message = "";
            var from = "";

            var mail = new {
                EmailTo = email,
                EmailFrom = from,
                Subject = subject,
                Content = message
            };

            var meta = new MetadataModel();
            meta.Metadata["AccessRequestEmail"] = JToken.Parse(mail.ToJSON());

            var resp = await appMgr.SendAccessRequestEmail(meta, details.EnterpriseAPIKey);

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> SetSelectedMap(Guid mapID)
        {
            ensureStateObject();

            var userMap = state.UserMaps.FirstOrDefault(x => x.ID == mapID);

            if (userMap != null)
            {
                state.SelectedUserMapID = mapID;
                
                // TODO: Filter results out a local collection of all locations 
                //var visibleLocations = await fetchVisibleUserLocations(details.Username, details.EnterpriseAPIKey, state.SelectedUserLayerIDs);

                state.VisibleUserLocations = limitUserLocationsGeographically(state.AllUserLocations, userMap.Coordinates)
                                            .Distinct()
                                            .ToList();

               //state.VisibleUserLocations = state.VisibleUserLocations.Distinct().ToList();
            }

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> ShareItinerary(List<Itinerary> itineraries, List<string> usernames)
        {
            ensureStateObject();

            var success = true;

            usernames.ForEach(
                (username) =>
                {
                    itineraries.ForEach(
                        (itinerary) =>
                        {
                            var result = amblGraph.ShareItinerary(details.Username, details.EnterpriseAPIKey, itinerary.ID, username).GetAwaiter().GetResult();

                            if (!result.Status)
                                success = false;
                        });
                });

            if (!success)
                state.Error = "General Error sharing itinerary.";

            state.Loading = false;

            return state;
        }

        public virtual async Task<UsersState> UnshareItinerary(List<Itinerary> itineraries, List<string> usernames)
        {
            ensureStateObject();

            var success = true;

            usernames.ForEach(
                (username) =>
                {
                    itineraries.ForEach(
                        (itinerary) =>
                        {
                            var result = amblGraph.UnshareItinerary(details.Username, details.EnterpriseAPIKey, itinerary.ID, username).GetAwaiter().GetResult();

                            if (!result.Status)
                                success = false;
                        });
                });

            if (!success)
                state.Error = "General Error unsharing itinerary.";
            
            state.Loading = false;

            return state;
        }
        #endregion

        #region Helpers

        // Returns the radius and center of a circle inscribed within the bounded box
        protected virtual Tuple<float,float,float> computeCircle(float lat1, float long1, float lat2, float long2) {
            var coord1 = new GeoCoordinate(Convert.ToDouble(lat1), Convert.ToDouble(long1));
            var coord2 = new GeoCoordinate(Convert.ToDouble(lat2), Convert.ToDouble(long2));

            var distanceMeters = Math.Abs(coord1.GetDistanceTo(coord2));

            // Attach modulus to adjust for international date line       
            float aveLong = (long2 < long1) ? 180 + ((long1 + long2) / 2) : (long1 + long2) / 2; 
            return new Tuple<float, float, float>(float.Parse((distanceMeters / 1609.344).ToString()), (lat1 + lat2) / 2, aveLong);
        }        

        protected virtual void ensureStateObject()
        {
            state.Error = "";

            if (state.SelectedUserLayerIDs == null)
                state.SelectedUserLayerIDs = new List<Guid>();

            if (state.UserLayers == null)
                state.UserLayers = new List<UserLayer>();

            if (state.UserMaps == null)
                state.UserMaps = new List<UserMap>();

            if (state.VisibleUserLocations == null)
                state.VisibleUserLocations = new List<UserLocation>();

            if (state.LocalSearchUserLocations == null)
                state.LocalSearchUserLocations = new List<UserLocation>();

            if (state.OtherSearchUserLocations == null)
                state.OtherSearchUserLocations = new List<UserLocation>();

            if (state.UserAlbums == null)
                state.UserAlbums = new List<UserAlbum>();

            if (state.UserItineraries == null)
                state.UserItineraries = new List<Itinerary>();

            state.UserTopLists = state.UserTopLists ?? new List<UserTopList>();

            state.AllUserLocations = state.AllUserLocations ?? new List<UserLocation>();
        }

        protected virtual async Task<List<UserAccolade>> fetchUserAccolades(string email, string entAPIKey, Guid locationId)
        {
            var userAccolades = new List<UserAccolade>();

            var accolades = await amblGraph.ListAccolades(email, entAPIKey, locationId);

            accolades.ForEach(
                (accolade) =>
                {
                    userAccolades.Add(mapUserAccolade(accolade, locationId));
                });

            return userAccolades;
        }

        protected virtual async Task<List<UserAlbum>> fetchUserAlbums(string email, string entAPIKey)
        {
            var userAlbums = new List<UserAlbum>();

            var albums = await amblGraph.ListAlbums(email, entAPIKey);

            albums.ForEach(
                (album) =>
                {
                    var photos = amblGraph.ListPhotos(email, entAPIKey, album.ID).GetAwaiter().GetResult();
                    userAlbums.Add(mapUserAlbum(album, photos));
                });

            return userAlbums;
        }

        protected virtual async Task<List<Itinerary>> fetchUserItineraries(string email, string entAPIKey)
        {
            var itineraries = await amblGraph.ListItineraries(email, entAPIKey);

            itineraries.ForEach(
                async (itinerary) =>
                {
                    var activityGroups = await amblGraph.ListActivityGroups(email, entAPIKey, itinerary.ID);

                    activityGroups.ForEach(
                        (activityGroup) =>
                        {
                            activityGroup.Activities = amblGraph.ListActivities(email, entAPIKey, itinerary.ID, activityGroup.ID).GetAwaiter().GetResult();
                        });
                });

            return itineraries;
        }

        protected virtual async Task<List<UserLayer>> fetchUserLayers(string email, string entAPIKey)
        {
            var userLayers = new List<UserLayer>();

            var layers = await amblGraph.ListLayers(email, entAPIKey);

            layers.ForEach(
                (layer) =>
                {
                    userLayers.Add(mapUserLayer(layer));
                });

            var sharedLayers = await amblGraph.ListSharedLayers(email, entAPIKey);

            sharedLayers.ForEach(
                (layerInfo) =>
                {
                    float[] coords = null;

                    var associatedMap = state.UserMaps.FirstOrDefault(x => x.ID == layerInfo.Item1.DefaultMapID);

                    if (associatedMap != null)
                        coords = associatedMap.Coordinates;

                    // Insert curated layer first
                    userLayers.Insert(0, mapUserLayer(layerInfo.Item1, layerInfo.Item2, coords));
                });

            return userLayers;
        }

        protected virtual async Task<List<UserMap>> fetchUserMaps(string email, string entAPIKey)
        {
            var userMaps = new List<UserMap>();

            var maps = await amblGraph.ListMaps(email, entAPIKey);

            maps.ForEach(
                (map) =>
                {
                    userMaps.Add(mapUserMap(map));
                });

            var sharedMaps = await amblGraph.ListSharedMaps(email, entAPIKey);

            sharedMaps.ForEach(
                (mapInfo) =>
                {
                    userMaps.Add(mapUserMap(mapInfo.Item1, mapInfo.Item2));
                });

            return userMaps;
        }

        // Need clarification on the behavior of this - is this supposed to pull back all locations across all layers? 
        protected virtual async Task<List<UserLocation>> fetchVisibleUserLocations(string email, string entAPIKey, List<Guid> layerIDs)
        {
            var userLocations = new List<UserLocation>();

            layerIDs.ForEach(
                (layerID) =>
                {   
                    var userLayerLocations = new List<UserLocation>();

                    var layer = state.UserLayers.FirstOrDefault(x => x.ID == layerID);

                    var locations = amblGraph.ListLocations(email, entAPIKey, layerID).GetAwaiter().GetResult();

                    locations.ForEach(
                        (location) =>
                        {
                            var loc = mapUserLocation(location, layerID, state.UserLayers.Any(x => x.ID == layerID && !x.Shared));
                            var accolades = fetchUserAccolades(email, entAPIKey, location.ID).GetAwaiter().GetResult();
                            loc.Accolades = accolades; 
                            userLayerLocations.Add(loc);                            
                        });

                    if (layer != null && layer.Coordinates != null)
                        userLayerLocations = userLayerLocations.Where(x => x.Latitude <= layer.Coordinates[0]
                                    && x.Latitude >= layer.Coordinates[2]
                                    && x.Longitude <= layer.Coordinates[1]
                                    && x.Longitude >= layer.Coordinates[3]).ToList();

                    userLocations.AddRange(userLayerLocations);
                });

            return userLocations;
        }

        protected virtual async Task<List<UserTopList>> fetchUserTopLists(string email, string entAPIKey, Guid layerId)
        {
            var userTopLists = new List<UserTopList>();

            var topLists = await amblGraph.ListTopLists(email, entAPIKey);

            topLists.ForEach(
                (topList) =>
                {
                    var locations = amblGraph.ListTopListLocations(email, entAPIKey, topList.ID).GetAwaiter().GetResult();
                    userTopLists.Add(mapUserTopList(topList, locations, layerId));
                });

            return userTopLists;

        }
        protected virtual async Task<ExcludedCurations> fetchUserExcludedCurations(string email, string entAPIKey)
        {
            var curations = await amblGraph.ListExcludedCurations(email, entAPIKey);

            return curations;

        }


        protected virtual List<UserLocation> limitUserLocationsByRadius(List<UserLocation> userLocations, float radius, float centerLat, float centerLong)
        {
            if (radius > 0)
            {
                var center = new GeoCoordinate(Convert.ToDouble(centerLat), Convert.ToDouble(centerLong));

                return userLocations.Where(x =>
                    {
                        var coord = new GeoCoordinate(Convert.ToDouble(x.Latitude), Convert.ToDouble(x.Longitude));

                        return float.Parse((Math.Abs(coord.GetDistanceTo(center)) / 1609.344).ToString()) <= radius;
                    }).ToList();
            }
            else
                return userLocations;
        }

        protected virtual List<UserLocation> limitUserLocationsBySearch(List<UserLocation> userLocations, string searchTerm)
        {
            if (!String.IsNullOrEmpty(searchTerm))
            {
                return userLocations.Where(x => x.Title.ToLower().Contains(searchTerm.ToLower())).ToList();
            }
            else
                return userLocations;
        }

        protected virtual List<UserLocation> limitUserLocationsGeographically(List<UserLocation> userLocations, float[] coordinates)
        {
            if (coordinates != null && coordinates.Count() == 4)
            {
                if (coordinates[1]<=coordinates[3]) {

                    //Accounts for the possibility that the bottom left coordinate has a greater longitude value 
                    //than the top right, due to it being on the opposite side of the international date line
                    var result =  userLocations.Where(x => x.Latitude <= coordinates[0]
                                                    && x.Latitude >= coordinates[2]
                                                    && x.Longitude <= 180.0
                                                    && x.Longitude >= coordinates[3])
                                    .Union(userLocations.Where(x => x.Latitude <= coordinates[0]
                                                    && x.Latitude >= coordinates[2]
                                                    && x.Longitude <= coordinates[1]
                                                    && x.Longitude >= -180.0)).ToList();
                    return result;
                } else {
                    var result =  userLocations.Where(x => x.Latitude <= coordinates[0]
                                                    && x.Latitude >= coordinates[2]
                                                    && x.Longitude <= coordinates[1]
                                                    && x.Longitude >= coordinates[3]).ToList();
                    return result;
                }
            }
            else
                return userLocations;
        }

        protected virtual List<UserPhoto> mapImageDataToUserPhotos(List<UserPhoto> photos, List<ImageMessage> images)
        {
            var photoCount = 0;

            photos.ForEach(
                (photo) =>
                {
                    var img = images.FirstOrDefault(x => QueryHelpers.ParseQuery(x.Headers)["ID"] == photo.ID);

                    if (img == null)
                        img = images[photoCount];

                    if (img != null)
                        photo.ImageData = img;

                    photoCount++;
                });

            return photos;
        }

        protected virtual UserAccolade mapUserAccolade(Accolade accolade, Guid locationId)
        {
            return new UserAccolade()
            {
                ID = accolade.ID,
                LocationID = locationId,
                Rank = accolade.Rank,
                Title = accolade.Title,
                Year = accolade.Year
            };
        }

        protected virtual UserAlbum mapUserAlbum(Album album, List<Photo> photos)
        {
            var userAlbum = new UserAlbum()
            {
                ID = album.ID,
                Photos = new List<UserPhoto>(),
                Title = album.Title
            };

            photos.ForEach(
                (photo) =>
                {
                    var userPhoto = mapUserPhoto(photo);
                    userAlbum.Photos.Add(userPhoto);
                });

            return userAlbum;
        }

        protected virtual UserLayer mapUserLayer(Layer layer)
        {
            return new UserLayer()
            {
                ID = layer.ID,
                Deletable = true,
                Shared = false,
                Title = layer.Title,
                InheritedID = layer.ID
            };
        }

        protected virtual UserLayer mapUserLayer(SharedLayer layer, Layer parent, float[] coordinates)
        {
            return new UserLayer()
            {
                ID = layer.ID,
                Coordinates = coordinates,
                Deletable = layer.Deletable,
                Shared = true,
                Title = layer.Title,
                InheritedID = parent.ID
            };
        }

        protected virtual UserLocation mapUserLocation(Location location, Guid layerID, bool userOwns)
        {
            return new UserLocation()
            {
                ID = location.ID,
                Address = location.Address,
                Country = location.Country,
                Deletable = userOwns,
                GoogleLocationName = location.GoogleLocationName,
                Icon = location.Icon,
                Instagram = location.Instagram,
                Latitude = location.Latitude,
                LayerID = layerID,
                Longitude = location.Longitude,
                State = location.State,
                Telephone = location.Telephone,
                Title = location.Title,
                Town = location.Town,
                Website = location.Website,
                ZipCode = location.ZipCode
            };
        }

        protected virtual UserMap mapUserMap(Map map)
        {
            bool hasCoords = false;
            var coords = map.Coordinates.Split(",");
            var fCoords = new float[4];

            if (coords.Count() == 4)
            {
                hasCoords = true;
                var count = 0;

                coords.ToList().ForEach(
                    (coord) =>
                    {
                        fCoords[count] = float.Parse(coord);
                        count++;
                    });
            }

            return new UserMap()
            {
                ID = map.ID,
                Coordinates = hasCoords ? fCoords : null,
                DefaultLayerID = map.DefaultLayerID,
                Deletable = true,
                Latitude = map.Latitude,
                Longitude = map.Longitude,
                Primary = map.Primary,
                Shared = false,
                Title = map.Title,
                Zoom = map.Zoom,
                InheritedID = map.ID
            };
        }

        protected virtual UserMap mapUserMap(SharedMap map, Map parent)
        {
            bool hasCoords = false;
            var coords = parent.Coordinates.Split(",");
            var fCoords = new float[4];

            if (coords.Count() == 4)
            {
                hasCoords = true;
                var count = 0;

                coords.ToList().ForEach(
                    (coord) =>
                    {
                        fCoords[count] = float.Parse(coord);
                        count++;
                    });
            }

            return new UserMap()
            {
                ID = map.ID,
                Coordinates = hasCoords ? fCoords : null,
                DefaultLayerID = parent.DefaultLayerID,
                Deletable = map.Deletable,
                Latitude = parent.Latitude,
                Longitude = parent.Longitude,
                Primary = map.Primary,
                Shared = true,
                Title = map.Title,
                Zoom = parent.Zoom,
                InheritedID = parent.ID
            };
        }

        protected virtual UserPhoto mapUserPhoto(Photo photo)
        {
            return new UserPhoto()
            {
                ID = photo.ID,
                Caption = photo.Caption,
                URL = photo.URL,
                LocationID = photo.LocationID
            };
        }

        protected virtual UserTopList mapUserTopList(TopList topList, List<Location> locations, Guid layerId)
        {
            var userTopList = new UserTopList()
            {
                ID = topList.ID,
                LocationList = new List<UserLocation>(),
                Title = topList.Title,
                OrderedValue = topList.OrderedValue
            };

            locations.ForEach(
                (location) =>
                {
                    var userLocation = mapUserLocation(location, layerId, true);
                    userTopList.LocationList.Add(userLocation);
                });

            return userTopList;
        }

        protected virtual List<UserLocation> removeUserLocationsByLayerID(List<UserLocation> userLocations, Guid layerID)
        {
            return userLocations.Where(x => x.LayerID != layerID).ToList();
        }
        #endregion
    }
}