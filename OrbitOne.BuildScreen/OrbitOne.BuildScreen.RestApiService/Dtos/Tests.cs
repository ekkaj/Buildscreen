using System.Collections.Generic;

namespace OrbitOne.BuildScreen.Models
{
    public class Test
    {
        public int Id { get; set; }
        public IList<RunStatistics> RunStatistics { get; set; }
        public int TotalTests { get; set; }
        public int PassedTests { get; set; }
    }

    public class RunStatistics
    {
        public string State { get; set; }
        public string Outcome { get; set; }
        public int Count { get; set; }
    }
}