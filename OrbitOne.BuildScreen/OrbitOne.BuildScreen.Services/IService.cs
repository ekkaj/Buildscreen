using System.Collections.Generic;
using System.Threading.Tasks;
using OrbitOne.BuildScreen.Models;

namespace OrbitOne.BuildScreen.Services
{
    public interface IService
    {
        List<BuildInfoDto> GetBuildInfoDtos();
        List<BuildInfoDto> GetBuildInfoDtosPolling(string filterDate);
    }
}