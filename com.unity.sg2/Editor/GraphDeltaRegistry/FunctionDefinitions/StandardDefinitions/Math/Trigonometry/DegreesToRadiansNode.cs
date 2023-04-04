using Usage = UnityEditor.ShaderGraph.GraphDelta.GraphType.Usage;

namespace UnityEditor.ShaderGraph.Defs
{
    internal class DegreesToRadiansNode : IStandardNode
    {
        public static string Name => "DegreesToRadians";
        public static int Version => 1;

        public static FunctionDescriptor FunctionDescriptor => new(
            Name,
            "Out = radians(In);",
            new ParameterDescriptor[]
            {
                new ParameterDescriptor("In", TYPE.Vector, Usage.In),
                new ParameterDescriptor("Out", TYPE.Vector, Usage.Out)
            }
        );

        public static NodeUIDescriptor NodeUIDescriptor => new(
            Version,
            Name,
            displayName: "Degrees To Radians",
            tooltip: "converts degrees to radians",
            category: "Math/Trigonometry",
            synonyms: new string[3] { "degtorad", "radians", "convert" },
            description: "pkg://Documentation~/previews/DegreesToRadians.md",
            parameters: new ParameterUIDescriptor[2] {
                new ParameterUIDescriptor(
                    name: "In",
                    displayName: string.Empty,
                    tooltip: "a value in degrees"
                ),
                new ParameterUIDescriptor(
                    name: "Out",
                    displayName: string.Empty,
                    tooltip: "the input converted to radians"
                )
            }
        );
    }
}
