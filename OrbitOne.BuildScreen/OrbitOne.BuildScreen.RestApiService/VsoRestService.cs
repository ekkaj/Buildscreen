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
        private const int DegreeOfParallelism = 4;

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
                Parallel.ForEach(teamProjects, new ParallelOptions { MaxDegreeOfParallelism = DegreeOfParallelism }, teamProject =>
                {
                    var tempListOfBuildsPerTeamProject = GetBuildsForPollingSince(teamProject.Name, teamProject.Id, sinceDateTime).ToList();
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

        private IEnumerable<BuildInfoDto> GetBuildsForPollingSince(string teamProjectName, string teamProjectId, DateTime finishTime)
        {
            List<BuildInfoDto> dtos = new List<BuildInfoDto>();
            try
            {
                var polledBuilds = GetPolledBuilds(teamProjectName, finishTime);
                Parallel.ForEach(polledBuilds, new ParallelOptions { MaxDegreeOfParallelism = DegreeOfParallelism }, build =>
                {
                    var buildInfoDto = new BuildInfoDto
                    {
                        TeamProject = teamProjectName,
                        Status = build.Result ?? build.Status,
                        Builddefinition = build.Definition.Name,
                        StartBuildDateTime = build.StartTime,
                        FinishBuildDateTime = build.FinishTime,
                        RequestedByName = build.RequestedFor.DisplayName,
                        RequestedByPictureUrl = build.RequestedFor.ImageUrl + "&size=2",
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

        private void TrySetTestResults(Build build, string teamProjectName, BuildInfoDto buildInfoDto)
        {
            if (build.Result != null &&
                   (
                   build.Result.Equals(Enum.GetName(typeof(StatusEnum.Statuses), StatusEnum.Statuses.partiallySucceeded)) ||
                   build.Result.Equals(Enum.GetName(typeof(StatusEnum.Statuses), StatusEnum.Statuses.failed)) 
                   )
                   )
            {
                SetTestDetails(buildInfoDto, teamProjectName, build.Uri);
            }
        }

        private IEnumerable<Build> GetPolledBuilds(string teamProjectName, DateTime finishTime)
        {
            var polledBuilds = new List<Build> { };
            try
            {
                var onFinishTimeBuilds = _helperClass.RetrieveTask<Build>(String.Format(_configurationRestService.RetrieveBuildsOnFinishtime,
                teamProjectName, String.Format(_configurationRestService.HourFormatRest, finishTime.Year, finishTime.Month, finishTime.Day, finishTime.Hour, finishTime.Minute))).Result;
                var inProgressBuilds =
                    _helperClass.RetrieveTask<Build>(String.Format(_configurationRestService.RetrieveBuildsInProgress,
                        teamProjectName)).Result;
                polledBuilds.AddRange(onFinishTimeBuilds);
                polledBuilds.AddRange(inProgressBuilds);
            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }
            return polledBuilds;
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
                        dtoList.AddRange(GetBuildDefinitions(teamProject.Name, teamProject.Id).ToList());
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

        private IEnumerable<BuildInfoDto> GetBuildDefinitions(string teamProjectName, string teamProjectId)
        {
            var dtoList = new List<BuildInfoDto> { };
            try
            {
                var builddefinitions = _helperClass.RetrieveTask<BuildDefinition>(
                String.Format(_configurationRestService.RetrieveBuildDefinitionsUrl, teamProjectName))
                .Result
                .Where(b => b.QueueStatus == null || !b.QueueStatus.Equals("disabled")) //it only returns a status when it's disabled (not tested for paused yet)
                .ToList();

                Parallel.ForEach(builddefinitions, new ParallelOptions { MaxDegreeOfParallelism = DegreeOfParallelism }, bd =>
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
                var latestBuild =
                    _helperClass
                        .RetrieveTask<Build>(
                        (String.Format(_configurationRestService.RetrieveLastBuildAsyncUrl, teamProjectName, bdId)))
                        .Result
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
                    var secondLastBuild = GetLastBuildTime(teamProjectName, latestBuild);
                        // _helperClass.RetrieveTask<Build>(
                        //(String.Format(_configurationRestService.RetrieveLastSuccessfulBuildUrl, teamProjectName, bdUri))).Result;

                    
                    if (secondLastBuild != null)
                    {
                        buildInfoDto.LastBuildTime = secondLastBuild.FinishTime - secondLastBuild.StartTime;
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