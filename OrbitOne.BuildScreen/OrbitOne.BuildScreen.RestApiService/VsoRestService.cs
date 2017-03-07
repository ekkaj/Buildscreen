using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using OrbitOne.BuildScreen.Configuration;
using OrbitOne.BuildScreen.Models;
using OrbitOne.BuildScreen.Services;

namespace OrbitOne.BuildScreen.RestApiService
{
    public class VsoRestService : IService
    {
        private readonly IConfigurationRestService _configurationRestService;
        private readonly IHelperClass _helperClass;

        /* This contrains the amount of parallel tasks, because there is nested
         * parallelism, there is a need to constrain this number. Testing has pointed out
         * that 4 is the best solution */
        private int DegreeOfParallelism = 1;

        public VsoRestService(IConfigurationRestService configurationRestService, IHelperClass helperClass)
        {
            _configurationRestService = configurationRestService;
            _helperClass = helperClass;
        }

        public List<BuildInfoDto> GetBuildInfoDtosPolling(String finishTimePoll)
        {
            

            var dtoPollList = new List<BuildInfoDto>();
            
            try
            {
                var sinceDateTime = DateTime.Now.Subtract(new TimeSpan(int.Parse(finishTimePoll), 0, 0)).ToUniversalTime();

                var teamProjects = _helperClass.RetrieveTask<TeamProject>(_configurationRestService.RetrieveProjectsAsyncUrl).Result;
                var allBuildDefinitions = new List<BuildDefinition>();

                foreach (var teamProject in teamProjects)
                {
                    var buildDefinitions = GetAllBuildDefintions(teamProject.Name);
                    allBuildDefinitions.AddRange(buildDefinitions);
                }

                Parallel.ForEach(teamProjects, new ParallelOptions { MaxDegreeOfParallelism = DegreeOfParallelism }, teamProject =>
                {
                    var tempListOfBuildsPerTeamProject = GetBuildsForPollingSince(teamProject.Name, teamProject.Id, sinceDateTime, allBuildDefinitions).ToList();
                    tempListOfBuildsPerTeamProject = tempListOfBuildsPerTeamProject.GroupBy(b => b.Id)
                        .Select(b => b.OrderByDescending(d => d.StartBuildDateTime).FirstOrDefault())
                        .ToList();

                    if (!tempListOfBuildsPerTeamProject.Any()) return;
                    lock (dtoPollList)
                    {
                        dtoPollList.AddRange(tempListOfBuildsPerTeamProject);
                    }
                });
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }

            return dtoPollList;
        }

        private IEnumerable<BuildInfoDto> GetBuildsForPollingSince(string teamProjectName, string teamProjectId, DateTime finishTime, List<BuildDefinition> buildDefinitions)
        {
            List<BuildInfoDto> dtos = new List<BuildInfoDto>();
            try
            {
                var polledBuilds = GetPolledBuilds(teamProjectName, finishTime, buildDefinitions);
                Parallel.ForEach(polledBuilds, new ParallelOptions { MaxDegreeOfParallelism = DegreeOfParallelism }, build =>
                {
                    var buildInfoDto = new BuildInfoDto
                    {
                        TeamProject = teamProjectName,
                        Status = build.Result ?? build.Status,
                        Builddefinition = build.Definition.Name,
                        StartBuildDateTime = build.StartTime,
                        FinishBuildDateTime = build.FinishTime,
                        RequestedByName = GetRequestedForName(build),
                        RequestedByPictureUrl = GetRequestedForImageUrl(build) + "&size=2",
                        TotalNumberOfTests = 0,
                        PassedNumberOfTests = 0,
                        BuildReportUrl = _helperClass.ConvertReportUrl(teamProjectName, build.Uri, true),
                        Id = "VSO" + teamProjectId + build.Definition.Id
                    };

                    if(buildInfoDto.RequestedByName.StartsWith("[DefaultCollection]"))
                    {
                        buildInfoDto.RequestedByName = "Service Account";
                    }

                    if (build.Status.Equals(Enum.GetName(typeof(StatusEnum.Statuses), StatusEnum.Statuses.inProgress)))
                    {
                        buildInfoDto.BuildReportUrl = _helperClass.ConvertReportUrl(teamProjectName, build.Uri, true);
                        var lastBuildTime = GetLastBuildTime(teamProjectName, build);
                        buildInfoDto.Status = StatusEnum.Statuses.inProgress.ToString();
                        if (lastBuildTime != null)
                        {
                            buildInfoDto.LastBuildTime = lastBuildTime.FinishTime - lastBuildTime.StartTime;
                        }
                    }

                    TrySetTestResults(build, teamProjectName, buildInfoDto);
                    
                    lock (dtos)
                    {
                        dtos.Add(buildInfoDto);
                    }
                });
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }
            return dtos;
        }

        private string GetRequestedForName(Build build)
        {
            if (build.RequestedFor != null)
            {
                return build.RequestedFor.DisplayName;
            }
            if (build.Requests != null && build.Requests.Any())
            {
                return build.Requests.First().RequestedFor.DisplayName;
            }
            return null;
        }

        private string GetRequestedForImageUrl(Build build)
        {
            if (build.RequestedFor != null)
            {
                return build.RequestedFor.ImageUrl;
            }
            if (build.Requests != null && build.Requests.Any())
            {
                return build.Requests.First().RequestedFor.ImageUrl;
            }
            return null;
        }

        private void TrySetTestResults(Build build, string teamProjectName, BuildInfoDto buildInfoDto)
        {
            if ((build.Result != null &&
                   (
                   build.Result.Equals(Enum.GetName(typeof(StatusEnum.Statuses), StatusEnum.Statuses.partiallySucceeded)) ||
                   build.Result.Equals(Enum.GetName(typeof(StatusEnum.Statuses), StatusEnum.Statuses.failed)) 
                   )) || build.Result == null && build.Status.Equals(Enum.GetName(typeof(StatusEnum.Statuses), StatusEnum.Statuses.partiallySucceeded))
                   )
            {
                SetTestDetails(buildInfoDto, teamProjectName, build.Uri);
            }
        }

        private IEnumerable<Build> GetPolledBuilds(string teamProjectName, DateTime finishTime, List<BuildDefinition> buildDefinitions)
        {
            var polledBuilds = new List<Build> { };
            try
            {
                var finishTimeMonth = finishTime.Month.ToString().Length == 1 ? "0" + finishTime.Month : finishTime.Month.ToString();
                var finishTimeDay = finishTime.Day.ToString().Length == 1 ? "0" + finishTime.Day : finishTime.Day.ToString();
                
                var finishTimeHour = finishTime.Hour.ToString().Length == 1 ? "0" + finishTime.Hour : finishTime.Hour.ToString();
                var finishTimeMinute = finishTime.Minute.ToString().Length == 1 ? "0" + finishTime.Minute : finishTime.Minute.ToString();

                var dateTimeFormat = String.Format(_configurationRestService.HourFormatRest, finishTime.Year, finishTimeMonth,
                    finishTimeDay, finishTimeHour, finishTimeMinute);

                var retrieveBuildsOnFinishtimeUri = String.Format(_configurationRestService.RetrieveBuildsOnFinishtime,
                    teamProjectName, dateTimeFormat, string.Join(",", buildDefinitions.Where(x => x.Type == "build").Select(x => x.Id)));

                var retrieveBuildsOnFinishtimeXamlBuildsUri = String.Format(_configurationRestService.RetrieveBuildsOnFinishtimeXamlBuilds,
                    teamProjectName, dateTimeFormat, string.Join(",", buildDefinitions.Where(x => x.Type == "xaml").Select(x => x.Id)));

                var onFinishTimeBuilds = _helperClass.RetrieveTask<Build>(retrieveBuildsOnFinishtimeUri).Result;
                var onFinishTimeXamlBuilds = _helperClass.RetrieveTask<Build>(retrieveBuildsOnFinishtimeXamlBuildsUri).Result;

                var inProgressBuilds =
                    _helperClass.RetrieveTask<Build>(String.Format(_configurationRestService.RetrieveBuildsInProgress,
                        teamProjectName)).Result;
                inProgressBuilds = inProgressBuilds.Where(x => x.FinishTime == default(DateTime)).ToArray();

                polledBuilds.AddRange(onFinishTimeBuilds);
                polledBuilds.AddRange(onFinishTimeXamlBuilds);
                polledBuilds.AddRange(inProgressBuilds);

                
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }
            //return polledBuilds;
             return GetMostSignificantBuilds(polledBuilds);
            
        }

        private IEnumerable<Build> GetMostSignificantBuilds(IEnumerable<Build> builds)
        {
            foreach (var buildsByDefinition in builds.GroupBy(x =>x .Definition.Id))
            {
                if (buildsByDefinition.Any(x => x.Status.Equals(Enum.GetName(typeof(StatusEnum.Statuses), StatusEnum.Statuses.inProgress))))
                {
                    yield return buildsByDefinition.Where(x => x.Status.Equals(Enum.GetName(typeof(StatusEnum.Statuses), StatusEnum.Statuses.inProgress))).OrderByDescending(x => x.StartTime).First();
                }
                else
                {
                    yield return buildsByDefinition.OrderByDescending(x => x.StartTime).First();
                }
                
                
            }
        }

        


        private Build GetLastBuildTime(string teamProjectName, Build build)
        {
            Build secondLastBuild = null;
            try
            {
                secondLastBuild = _helperClass.RetrieveTask<Build>(String.Format(_configurationRestService.RetrieveLastSuccessfulBuildUrl, teamProjectName, build.Definition.Id)).Result.FirstOrDefault() ??
                                  _helperClass.RetrieveTask<Build>(String.Format(_configurationRestService.RetriveLastPartiallyOrFailedUrl, teamProjectName, build.Definition.Id)).Result.FirstOrDefault();
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }

            return secondLastBuild;
        }


        public List<BuildInfoDto> GetBuildInfoDtos()
        {
            var dtoList = new List<BuildInfoDto>();
            try
            {
                var teamProjects = _helperClass.RetrieveTask<TeamProject>(_configurationRestService.RetrieveProjectsAsyncUrl).Result;
                Parallel.ForEach(teamProjects, new ParallelOptions { MaxDegreeOfParallelism = DegreeOfParallelism }, teamProject =>
                {
                    lock (dtoList)
                    {
                        dtoList.AddRange(GetBuildInfoDtos(teamProject.Name, teamProject.Id).ToList());
                    }
                });
                if (!dtoList.Any())
                {
                    throw new ObjectNotFoundException("VSO did not return any results.");
                }
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }

            return dtoList;
        }

        private IEnumerable<BuildDefinition> GetAllBuildDefintions(string teamProjectName)
        {
            var buildDefinitions = _helperClass.RetrieveTask<BuildDefinition>(
                String.Format(_configurationRestService.RetrieveBuildDefinitionsUrl, teamProjectName))
                .Result
                .Where(b => b.QueueStatus == null || !b.QueueStatus.Equals("disabled")) //it only returns a status when it's disabled (not tested for paused yet)
                .ToList();

            return buildDefinitions;
        }

        private IEnumerable<BuildInfoDto> GetBuildInfoDtos(string teamProjectName, string teamProjectId)
        {
            var dtoList = new List<BuildInfoDto> { };
            try
            {
                var buildDefinitions = GetAllBuildDefintions(teamProjectName);

                Parallel.ForEach(buildDefinitions, new ParallelOptions { MaxDegreeOfParallelism = DegreeOfParallelism }, bd =>
                {
                    var dto = GetLatestBuild(teamProjectName, bd.Id, bd.Uri, bd.Name, teamProjectId);
                    if (dto != null)
                    {
                        lock (dtoList)
                        {
                            dtoList.Add(dto);
                        }
                    }
                });
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }


            return dtoList;
        }
        private BuildInfoDto GetLatestBuild(string teamProjectName, string bdId, string bdUri, string bdName, string teamProjectId)
        {

            BuildInfoDto buildInfoDto = null;
            try
            {
                var latestBuilds = _helperClass
                    .RetrieveTask<Build>(
                        (String.Format(_configurationRestService.RetrieveLastBuildAsyncUrl, teamProjectName, bdId)))
                    .Result;

                
                var latestBuild =latestBuilds.OrderByDescending(x => x.StartTime)
                        .FirstOrDefault();

                if (latestBuild == null) return null;
                buildInfoDto = new BuildInfoDto
                {
                    TeamProject = teamProjectName,
                    Status = latestBuild.Result ?? latestBuild.Status,
                    Builddefinition = bdName,
                    StartBuildDateTime = latestBuild.StartTime,
                    FinishBuildDateTime = latestBuild.FinishTime,
                    RequestedByName = latestBuild.RequestedFor.DisplayName,
                    RequestedByPictureUrl = latestBuild.RequestedFor.ImageUrl + "&size=2",
                    TotalNumberOfTests = 0,
                    PassedNumberOfTests = 0,
                    BuildReportUrl = _helperClass.ConvertReportUrl(teamProjectName, latestBuild.Uri, true),
                    Id = "VSO" + teamProjectId + bdId
                };

                if (buildInfoDto.RequestedByName.StartsWith("[DefaultCollection]"))
                {
                    buildInfoDto.RequestedByName = "Service Account";
                }

                if (latestBuild.Status.Equals(Enum.GetName(typeof(StatusEnum.Statuses), StatusEnum.Statuses.inProgress)))
                {
                    buildInfoDto.BuildReportUrl = _helperClass.ConvertReportUrl(teamProjectName, latestBuild.Uri, true);
                    buildInfoDto.Status = StatusEnum.Statuses.inProgress.ToString();

                    TimeSpan latestBuildTimeSpan;
                    if (TryGetLastBuildTimeSpan(teamProjectName, latestBuild, out latestBuildTimeSpan))
                    {
                        buildInfoDto.LastBuildTime = latestBuildTimeSpan;
                    }
                }
                
                TrySetTestResults(latestBuild, teamProjectName, buildInfoDto);
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }

            return buildInfoDto;
        }

        private bool TryGetLastBuildTimeSpan(string teamProjectName, Build latestBuild,  out TimeSpan lastBuildTimeSpan)
        {
            var secondLastBuild = GetLastBuildTime(teamProjectName, latestBuild);

            if (secondLastBuild != null)
            {
                lastBuildTimeSpan = secondLastBuild.FinishTime - secondLastBuild.StartTime;
                return true;
            }

            lastBuildTimeSpan  = TimeSpan.Zero;
            return false;
        }
        

        private IReadOnlyCollection<TestResult> GetTestResults(string teamProjectName, string buildUri)
        {
            TestResult[] result = { };
            try
            {
                var runs =
                _helperClass.RetrieveTask<Test>(String.Format(_configurationRestService.RetrieveRunsAsyncUrl, teamProjectName, HttpUtility.UrlEncode(buildUri)))
                .Result;
                if (!runs.Any()) return null;
                var runResult = runs.Max(t => t.Id);
                result = _helperClass.RetrieveTask<TestResult>(String.Format(_configurationRestService.RetrieveTestsAsyncUrl,
                    teamProjectName, runResult)).Result;
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }
            return result;
        }

        private void SetTestDetails(BuildInfoDto buildInfoDto, string teamProjectName, string buildUri)
        {
            var readOnlyCollection = GetTestRunDetails(teamProjectName, buildUri);

            var totalPassedTests = 0;
            var totalNumberOfTests = 0;


            if (readOnlyCollection.All(x => x.RunStatistics != null))
            {
                var runStatisticses = readOnlyCollection.SelectMany(x => x.RunStatistics).ToList();
                totalPassedTests = runStatisticses.Where(x => x.Outcome == StatusEnum.RunStatisticsStatus.Passed.ToString()).Sum(x => x.Count);
                totalNumberOfTests = runStatisticses.Sum(x => x.Count);
            }
            else
            {
                totalPassedTests = readOnlyCollection.Sum(x => x.PassedTests);
                totalNumberOfTests = readOnlyCollection.Sum(x => x.TotalTests);
            }

            buildInfoDto.TotalNumberOfTests = totalNumberOfTests;
            buildInfoDto.PassedNumberOfTests = totalPassedTests;
            if (totalNumberOfTests > 0 && totalNumberOfTests != totalPassedTests)
            {
                buildInfoDto.Status = StatusEnum.Statuses.partiallySucceeded.ToString();
            }
        }

        private IReadOnlyCollection<Test> GetTestRunDetails(string teamProjectName, string buildUri)
        {
            try
            {
                var runsTask =
                _helperClass.RetrieveTask<Test>(String.Format(_configurationRestService.RetrieveRunsAsyncUrl, teamProjectName, HttpUtility.UrlEncode(buildUri)));
                runsTask.Wait();
                var runs = runsTask.Result;
                return runs;
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }
            
        }


    }
}