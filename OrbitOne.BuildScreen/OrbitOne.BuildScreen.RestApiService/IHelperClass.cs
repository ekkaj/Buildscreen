using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace OrbitOne.BuildScreen.RestApiService
{
    public interface IHelperClass
    {
        Task<T[]> RetrieveTask<T>(string formattedUrl);
        string ConvertReportUrl(string teamProjectName, string buildUri, Boolean summary);
        HttpClient CreateAuthenticationClient(string uri, string username, string password);
        bool IsOnPremisesVSO(string uri);
    }
}