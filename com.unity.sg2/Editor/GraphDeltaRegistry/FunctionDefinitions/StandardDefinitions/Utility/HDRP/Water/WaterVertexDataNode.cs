using Usage = UnityEditor.ShaderGraph.GraphDelta.GraphType.Usage;

namespace UnityEditor.ShaderGraph.Defs
{
    internal class WaterVertexDataNode : IStandardNode
    {
        public static string Name => "WaterVertexData";
        public static int Version => 1;

        public static FunctionDescriptor FunctionDescriptor => new(
            Name,
@"PositionWS = GetWaterVertexPosition(pos);
NormalOS = GetWaterVertexNormal(norm);",
            new ParameterDescriptor[]
            {
                new ParameterDescriptor("pos", TYPE.Vec3, Usage.Local, REF.WorldSpace_Position),
                new ParameterDescriptor("norm", TYPE.Vec3, Usage.Local, REF.WorldSpace_Normal),
                new ParameterDescriptor("PositionOS", TYPE.Vec3, Usage.Out),
                new ParameterDescriptor("NormalOS", TYPE.Vec3, Usage.Out)
            }
        );

        public static NodeUIDescriptor NodeUIDescriptor => new(
            Version,
            Name,
            displayName: "Water Vertex Data",
            tooltip: "",
            category: "Utility/HDRP/Water",
            description: "pkg://Documentation~/previews/WaterVertexData.md",
            synonyms: new string[1] { "Compute Water Vertex Data" },
            hasPreview: false,
            parameters: new ParameterUIDescriptor[] {
                new ParameterUIDescriptor(
                    name: "PositionOS",
                    displayName: "Position OS",
                    tooltip: ""
                ),
                new ParameterUIDescriptor(
                    name: "NormalOS",
                    displayName: "Normal OS",
                    tooltip: ""
                )
            }
        );
    }
}

