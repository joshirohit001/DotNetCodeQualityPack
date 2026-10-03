using System;

namespace DotNetCodeQualityPack
{
    /// <summary>GUIDs from VSCommandTable.vsct. Keep both files in sync.</summary>
    internal sealed partial class PackageGuids
    {
        public const string DotNetCodeQualityPackString = "def565d7-52f0-4ab0-8c41-d2b92b53bd69";
        public static readonly Guid DotNetCodeQualityPack = new Guid(DotNetCodeQualityPackString);
    }

    /// <summary>Command and menu IDs from VSCommandTable.vsct. Keep both files in sync.</summary>
    internal sealed partial class PackageIds
    {
        public const int ToolsMenuGroup = 0x0001;
        public const int SolutionContextGroup = 0x0002;
        public const int CodeQualityMenu = 0x0010;
        public const int CodeQualityMenuGroup = 0x0020;
        public const int CodeQualityFixGroup = 0x0021;
        public const int CheckSolutionCommand = 0x0100;
        public const int ApplyStandardsCommand = 0x0101;
        public const int RemoveStandardsCommand = 0x0102;
        public const int AutoFixCommand = 0x0103;
        public const int UndoAutoFixCommand = 0x0104;
        public const int OpenFixerCommand = 0x0105;
        public const int OpenTeamRulesCommand = 0x0106;
        public const int EditorContextGroup = 0x0003;
        public const int OpenCodeCompassCommand = 0x0107;
        public const int ReviewWithCodeCompassCommand = 0x0108;
    }
}
