using System;
namespace AutoTest.Core
{
	public class RunnerExe
	{
		public string Exe { get; private set; }
		public string Version { get; private set; }
		
		public RunnerExe(string exe, string version)
		{
			Exe = exe;
			Version = version;
		}
		
		public override bool Equals(object obj)
		{
			return GetHashCode().Equals(obj.GetHashCode());
		}
		
		public override int GetHashCode ()
		{
			return (Exe + "|" + Version).GetHashCode();
		}
	}
}

