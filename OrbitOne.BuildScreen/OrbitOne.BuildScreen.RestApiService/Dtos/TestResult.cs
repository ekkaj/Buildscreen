namespace OrbitOne.BuildScreen.Models
{
    public class TestResult
    {
        public string Outcome { get; set; }
    }

    public class TestRunDetails
    {
        public int PassedTests { get; set; }
        public int TotalTests { get; set; }
    }
}
