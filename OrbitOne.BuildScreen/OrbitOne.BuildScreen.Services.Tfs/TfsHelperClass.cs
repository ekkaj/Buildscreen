using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.TeamFoundation.Client;
using OrbitOne.BuildScreen.Configuration;

namespace OrbitOne.BuildScreen.Services.Tfs
{
    public class TfsHelperClass : ITfsHelperClass
    {
        private readonly IServiceConfig _configurationTfsService;

        /* URL-part for a summary on Team Foundation Server */
        //Summary string for TFS 2015
        private const string SummaryString = "/_build#_a=summary&buildId=";

        public TfsHelperClass(IServiceConfig configurationTfsService)
        {
            _configurationTfsService = configurationTfsService;
        }
        public TfsConfigurationServer GetTfsServer()
        {
            TfsConfigurationServer server = null;
            try
            {
                var tfsUri = new Uri(_configurationTfsService.Uri);
                var credentials = new TfsClientCredentials(new WindowsCredential(new NetworkCredential(_configurationTfsService.Username, _configurationTfsService.Password)));
                server = new TfsConfigurationServer(tfsUri, credentials);
                server.EnsureAuthenticated();

            }
            catch (Exception e)
            {
                LogService.WriteError(e);
                throw;
            }
            return server;
        }

        public string GetReportUrl(string tpc, string tp, string buildUri)
        {
            return tpc + "/" + tp + SummaryString + Regex.Match(buildUri, @"\d+").Value;
        }

        public string GetImageUrl(string teamProjectCollectionUrl, string requestedByIdentifier)
        {
            var identifier = HttpUtility.UrlEncode(requestedByIdentifier);

            var path = string.Format("_api/_common/IdentityImage?id=&identifier={0}&resolveAmbiguous=false&identifierType=0&__v=5", identifier);
            var imageUrl = teamProjectCollectionUrl + "/" + path;
            return imageUrl;
        }
    }
}
