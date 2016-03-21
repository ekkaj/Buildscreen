using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Build.Client;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.Framework.Client;
using Microsoft.TeamFoundation.Framework.Common;
using Microsoft.TeamFoundation.Server;
using Microsoft.TeamFoundation.TestManagement.Client;
using OrbitOne.BuildScreen.Models;
using Build = Microsoft.TeamFoundation.Build.WebApi.Build;
using BuildQueryOrder = Microsoft.TeamFoundation.Build.Client.BuildQueryOrder;
using BuildStatus = Microsoft.TeamFoundation.Build.Client.BuildStatus;

namespace OrbitOne.BuildScreen.Services.Tfs
{
    public class TfsService : IService
    {
        private readonly ITfsHelperClass _helperClass;

        public TfsService(ITfsHelperClass tfsHelperClass)
        {
           _helperClass = tfsHelperClass;
        }

        public List<BuildInfoDto> GetBuildInfoDtos()
        {
            var buildInfoDtos = new ConcurrentBag<BuildInfoDto>();
            try
            {
                
                var tfsServer = _helperClass.GetTfsServer();
                // Get the catalog of team project collections
                var teamProjectCollectionNodes = tfsServer.CatalogNode.QueryChildren(
                    new[] { CatalogResourceTypes.ProjectCollection }, false, CatalogQueryOptions.None);
                var parallelOptions = new ParallelOptions {MaxDegreeOfParallelism = 1};
                Parallel.ForEach(teamProjectCollectionNodes, parallelOptions, teamProjectCollectionNode =>
                {
                    
                        var task = GetBuildInfoDtosPerTeamProject(teamProjectCollectionNode, tfsServer, DateTime.MinValue);
                        task.ConfigureAwait(false);
                        task.Wait();    
                        var buildInfos = task.Result;

                        foreach (var buildInfoDto in buildInfos)
                        {
                            buildInfoDtos.Add(buildInfoDto);
                        }
                        
                    
                });
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }

            return buildInfoDtos.ToList();
        }

        public List<BuildInfoDto> GetBuildInfoDtosPolling(String filterDate)
        {
            var buildInfoDtos = new List<BuildInfoDto>();
            try
            {
                var sinceDateTime = DateTime.Now.Subtract(new TimeSpan(int.Parse(filterDate), 0, 0));


                var tfsServer = _helperClass.GetTfsServer();

                // Get the catalog of team project collections
                var teamProjectCollectionNodes = tfsServer.CatalogNode.QueryChildren(
                    new[] { CatalogResourceTypes.ProjectCollection }, false, CatalogQueryOptions.None);

                var parallelOptions = new ParallelOptions() {MaxDegreeOfParallelism = 1};
                Parallel.ForEach(teamProjectCollectionNodes, parallelOptions, teamProjectCollectionNode =>
                {
                    var taskBuidInfos = GetBuildInfoDtosPerTeamProject(teamProjectCollectionNode, tfsServer, sinceDateTime);
                    taskBuidInfos.ConfigureAwait(false);
                    taskBuidInfos.Wait();

                    lock (buildInfoDtos)
                    {
                        buildInfoDtos.AddRange(taskBuidInfos.Result);
                    }
                });
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }

            return buildInfoDtos;
        }

        private async Task<List<BuildInfoDto>>  GetBuildInfoDtosPerTeamProject(CatalogNode teamProjectCollectionNode,
            TfsConfigurationServer tfsServer, DateTime filterDate)
        {
            var buildInfoDtos = new List<BuildInfoDto>();
            try
            {
                // Use the InstanceId property to get the team project collection
                var collectionId = new Guid(teamProjectCollectionNode.Resource.Properties["InstanceId"]);
                var teamProjectCollection = tfsServer.GetTeamProjectCollection(collectionId);

                var buildServer = (IBuildServer)teamProjectCollection.GetService(typeof(IBuildServer));
                var testService = teamProjectCollection.GetService<ITestManagementService>();



                // Get a catalog of team projects for the collection


                if (tfsServer.ServerDataProvider.ServerVersion == null)
                {


                    var teamProjectNodes = teamProjectCollectionNode.QueryChildren(new[] {CatalogResourceTypes.TeamProject}, false, CatalogQueryOptions.None);

                    // List the team projects in the collection
                    Parallel.ForEach(teamProjectNodes, teamProjectNode =>
                    {
                        var buildDefinitionList = buildServer.QueryBuildDefinitions(teamProjectNode.Resource.DisplayName).ToList();

                        lock (buildInfoDtos)
                        {
                            buildInfoDtos.AddRange(GetBuildInfoDtosPerBuildDefinition(buildDefinitionList, buildServer, teamProjectNode, teamProjectCollection, testService, filterDate));
                        }
                    });
                }
                else
                {
                    var commonStructureService = teamProjectCollection.GetService<ICommonStructureService>();
                    var httpClient = teamProjectCollection.GetClient<BuildHttpClient>();
                    var listAllProjects = commonStructureService.ListAllProjects().ToList();
                    foreach (var project in listAllProjects)
                    {
                        var definitionReferences = await httpClient.GetDefinitionsAsync(project: project.Name).ConfigureAwait(false);
                        
                        var buildInfos = await GetBuildInfoDtosPerBuildDefinitionRest(teamProjectCollection, definitionReferences, httpClient);
                        lock (buildInfoDtos)
                        {
                            buildInfoDtos.AddRange(buildInfos);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }


            return await Task.FromResult(buildInfoDtos);
        }

        private IEnumerable<BuildInfoDto> GetBuildInfoDtosPerBuildDefinition(List<IBuildDefinition> buildDefinitionList,
            IBuildServer buildServer, CatalogNode teamProjectNode,
            TfsTeamProjectCollection teamProjectCollection, ITestManagementService testService, DateTime filterDate)
        {
            var buildDtos = new List<BuildInfoDto>();
            try
            {
                Parallel.ForEach(buildDefinitionList, def =>
                {
                    var build = GetBuild(buildServer, teamProjectNode, def, filterDate);

                    if (build == null) return;

                    var buildInfoDto = new BuildInfoDto
                    {
                        Builddefinition = def.Name,
                        FinishBuildDateTime = build.FinishTime,
                        LastBuildTime = new TimeSpan(),
                        PassedNumberOfTests = 0,
                        RequestedByName = build.RequestedFor,
                        RequestedByPictureUrl = _helperClass.GetImageUrl(teamProjectCollection.Uri.ToString(), build.Requests.First().RequestedFor),
                        StartBuildDateTime = build.StartTime,
                        
                        Status = Char.ToLowerInvariant(build.Status.ToString()[0]) + build.Status.ToString().Substring(1),
                        TeamProject = teamProjectNode.Resource.DisplayName,
                        TeamProjectCollection = teamProjectCollection.Name,
                        TotalNumberOfTests = 0,
                        Id = "TFS" + teamProjectNode.Resource.Identifier + def.Id,
                        BuildReportUrl = _helperClass.GetReportUrl(teamProjectCollection.Uri.ToString(), teamProjectNode.Resource.DisplayName, build.Uri.OriginalString)
                    };
                    //Retrieve testruns
                    var testResults = GetTestResults(teamProjectNode, testService, build);

                    if (testResults.ContainsKey("PassedTests"))
                    {
                        buildInfoDto.PassedNumberOfTests = testResults["PassedTests"];
                        buildInfoDto.TotalNumberOfTests = testResults["TotalTests"];
                    }
                    //Add last succeeded build if in progress
                    if (build.Status == BuildStatus.InProgress)
                    {
                        buildInfoDto.LastBuildTime = GetLastSuccesfulBuildTime(buildServer, teamProjectNode, def);
                    }
                    lock (buildDtos)
                    {
                        buildDtos.Add(buildInfoDto);
                    }
                });
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }

            return buildDtos;
        }

        private async Task<IEnumerable<BuildInfoDto>> GetBuildInfoDtosPerBuildDefinitionRest(TfsTeamProjectCollection teamProjectCollection, IList<DefinitionReference> definitionReferences, BuildHttpClient httpClient)
        {
            try
            {

           
            var buildInfoDtos = new List<BuildInfoDto>();
            foreach (var definitionReference in definitionReferences)
            {
                var builds = await httpClient.GetBuildsAsync(top: 1, project: definitionReference.Project.Id, minFinishTime: DateTime.Now.AddMonths(-6),  definitions: new[] { definitionReference.Id }).ConfigureAwait(false);
                var build = builds.FirstOrDefault();

                if (build == null) continue;

                var buildInfoDto = new BuildInfoDto();

                buildInfoDto.Builddefinition = definitionReference.Name;
                buildInfoDto.FinishBuildDateTime = build.FinishTime.GetValueOrDefault();
                buildInfoDto.LastBuildTime = new TimeSpan();
                buildInfoDto.PassedNumberOfTests = 0;
                buildInfoDto.RequestedByName = build.RequestedFor.DisplayName;
                buildInfoDto.RequestedByPictureUrl = build.RequestedFor.ImageUrl;
                buildInfoDto.StartBuildDateTime = build.StartTime.GetValueOrDefault();
                buildInfoDto.Status = build.Result.HasValue ?
                        Char.ToLowerInvariant(build.Result.ToString()[0]) + build.Result.ToString().Substring(1)
                    : Char.ToLowerInvariant(build.Status.ToString()[0]) + build.Status.ToString().Substring(1);
                    
                buildInfoDto.TeamProject = definitionReference.Project.Name;
                buildInfoDto.TeamProjectCollection = teamProjectCollection.DisplayName;
                buildInfoDto.TotalNumberOfTests = 0;
                buildInfoDto.Id = "TFS" + definitionReference.Project.Id + definitionReference.Id;

                if (build.Uri == null)
                {
                    continue;
                }

                buildInfoDto.BuildReportUrl = _helperClass.GetReportUrl(teamProjectCollection.Uri != null ? teamProjectCollection.Uri.ToString() : "", definitionReference.Project.Name, build.Uri.ToString());

                if (build.Result.HasValue && build.Result.Value == BuildResult.PartiallySucceeded)
                {
                    var testResults = GetTestResultsRest(teamProjectCollection, definitionReference, build);

                    if (testResults.ContainsKey("PassedTests"))
                    {
                        buildInfoDto.PassedNumberOfTests = testResults["PassedTests"];
                        buildInfoDto.TotalNumberOfTests = testResults["TotalTests"];
                    }
                }

                //Add last succeeded build if in progress
                if (build.Status.HasValue && build.Status.Value == Microsoft.TeamFoundation.Build.WebApi.BuildStatus.InProgress)
                {
                    buildInfoDto.LastBuildTime = await GetLastSuccesfulBuildTimeRest(teamProjectCollection, definitionReference, httpClient).ConfigureAwait(false);
                }

                lock (buildInfoDtos)
                {
                    buildInfoDtos.Add(buildInfoDto);
                }
            }

            return buildInfoDtos;
            }
            catch (Exception exception)
            {
                LogService.WriteError(exception);
                throw;
            }
        }

       

        private IBuildDetail GetBuild(IBuildServer buildServer, CatalogNode teamProjectNode, IBuildDefinition def, DateTime filterDate)
        {
            IBuildDetail build = null;
            try
            {
                var buildDetailSpec = buildServer.CreateBuildDetailSpec(teamProjectNode.Resource.DisplayName, def.Name);

                buildDetailSpec.MaxBuildsPerDefinition = 1;
                buildDetailSpec.QueryOrder = BuildQueryOrder.FinishTimeDescending;
                buildDetailSpec.MinFinishTime = filterDate;
                buildDetailSpec.InformationTypes = null;
                buildDetailSpec.QueryOptions = QueryOptions.Definitions | QueryOptions.BatchedRequests;

                build = buildServer.QueryBuilds(buildDetailSpec).Builds.FirstOrDefault();
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }
            return build;
        }

        private async Task<TimeSpan> GetLastSuccesfulBuildTimeRest(TfsTeamProjectCollection teamProjectCollection, DefinitionReference definitionReference, BuildHttpClient httpClient)
        {
            var buildTime = new TimeSpan();

            try
            {
                var lastSuccessFullList = await httpClient.GetBuildsAsync(definitions: new List<int> { definitionReference.Id }, project: definitionReference.Project.Id, resultFilter: BuildResult.Succeeded,  statusFilter: Microsoft.TeamFoundation.Build.WebApi.BuildStatus.Completed, top: 1).ConfigureAwait(false);
                var build = lastSuccessFullList.FirstOrDefault();

                if (build == null)
                {
                    var lastPartiallySucceededList =   await httpClient.GetBuildsAsync(definitions: new List<int> { definitionReference.Id }, project: definitionReference.Project.Id, resultFilter: BuildResult.PartiallySucceeded, statusFilter: Microsoft.TeamFoundation.Build.WebApi.BuildStatus.Completed, top: 1).ConfigureAwait(false);
                    build = lastPartiallySucceededList.FirstOrDefault();
                }

                if (build != null)
                {
                    buildTime = build.FinishTime.GetValueOrDefault() - build.StartTime.GetValueOrDefault();
                }
                

            }
            catch (Exception)
            {
                
                throw;
            }
            return buildTime;
        }

        private TimeSpan GetLastSuccesfulBuildTime(IBuildServer buildServer, CatalogNode teamProjectNode,
            IBuildDefinition def)
        {
            var buildTime = new TimeSpan();
            try
            {
                var inProgressBuildDetailSpec = buildServer.CreateBuildDetailSpec(teamProjectNode.Resource.DisplayName, def.Name);

                inProgressBuildDetailSpec.Status = BuildStatus.Succeeded;
                inProgressBuildDetailSpec.MaxBuildsPerDefinition = 1;
                inProgressBuildDetailSpec.QueryOrder = BuildQueryOrder.FinishTimeDescending;
                inProgressBuildDetailSpec.InformationTypes = null;
                inProgressBuildDetailSpec.QueryOptions = QueryOptions.None;

                var lastSuccesfulBuild = buildServer.QueryBuilds(inProgressBuildDetailSpec).Builds.FirstOrDefault();

                if (lastSuccesfulBuild != null)
                {
                    buildTime = lastSuccesfulBuild.FinishTime - lastSuccesfulBuild.StartTime;
                }
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }
            return buildTime;
        }

        private Dictionary<String, int> GetTestResults(CatalogNode teamProjectNode, ITestManagementService testService,
            IBuildDetail build)
        {
            var testResults = new Dictionary<string, int>();
            try
            {
                var testProject = testService.GetTeamProject(teamProjectNode.Resource.DisplayName);

                int passedTests = 0;
                int totalTests = 0;
                bool addTestResults = false;
                foreach (var testRun in testProject.TestRuns.ByBuild(build.Uri).ToList())
                {

                    if (testRun != null)
                    {
                        passedTests += testRun.PassedTests;
                        totalTests += testRun.TotalTests;
                        addTestResults = true;
                    }
                }

                if (addTestResults)
                {
                    testResults.Add("PassedTests", passedTests);
                    testResults.Add("TotalTests", totalTests);
                }

                //var testRun = testProject.TestRuns.ByBuild(build.Uri);

                //if (testRun != null)
                //{
                //    testResults.Add("PassedTests", testRun.PassedTests);
                //    testResults.Add("TotalTests", testRun.TotalTests);
                //}
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }
            return testResults;
        }

        private Dictionary<string, int> GetTestResultsRest(TfsTeamProjectCollection teamProjectCollection, DefinitionReference definitionReference, Build build)
        {
            var testResults = new Dictionary<string, int>();
            try
            {
                var testManagementService = teamProjectCollection.GetService<ITestManagementService>();

                var testManagementTeamProject = testManagementService.GetTeamProject(definitionReference.Project.Name);
                var testRuns = testManagementTeamProject.TestRuns.ByBuild(build.Uri).ToList();

                if (testRuns.Any())
                {
                    var totalTests = testRuns.Sum(x => x.TotalTests);
                    var totalPassedTests = testRuns.Sum(x => x.PassedTests);

                    testResults.Add("PassedTests", totalPassedTests);
                    testResults.Add("TotalTests", totalTests);
                }
                
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }
            return testResults;


        }
    }
}