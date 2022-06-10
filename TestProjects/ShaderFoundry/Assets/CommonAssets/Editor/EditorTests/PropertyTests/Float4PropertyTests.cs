using System.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityEditor.ShaderFoundry.UnitTests
{
    class Float4PropertyTests : BlockTestRenderer
    {
        Vector4PropertyBlockBuilder BuildWithoutNameOverrides(string defaultValue)
        {
            var propBuilder = new Vector4PropertyBlockBuilder
            {
                PropertyAttribute = new PropertyAttributeData { DefaultValue = defaultValue },
                FieldName = "Float4Field",
            };
            return propBuilder;
        }

        Vector4PropertyBlockBuilder BuildWithNameOverrides(string defaultValue)
        {
            var propBuilder = BuildWithoutNameOverrides(defaultValue);
            propBuilder.PropertyAttribute.UniformName = "_Value";
            propBuilder.PropertyAttribute.DisplayName = "Value";
            return propBuilder;
        }

        [UnityTest]
        public IEnumerator Float4Property_DefaultPropertyValueUsed_IsExpectedColor()
        {
            var expectedColor = new Color(1, 1, 1, 1);

            var container = CreateContainer();
            var propBuilder = BuildWithoutNameOverrides("(1, 1, 1, 1)");
            var block = propBuilder.Build(container, container._float4);

            TestSurfaceBlockIsConstantColor(container, propBuilder.BlockName, block, expectedColor);
            yield break;
        }

        [UnityTest]
        public IEnumerator Float4Property_MaterialColorSet_IsExpectedColor()
        {
            var inputValue = new Vector4(0.1f, 0.2f, 0.3f, 0.4f);
            var expectedColor = new Color(inputValue.x, inputValue.y, inputValue.z, inputValue.w);

            var container = CreateContainer();
            var propBuilder = BuildWithNameOverrides("(1, 1, 1, 1)");
            var block = propBuilder.Build(container, container._float4);

            SetupMaterialDelegate materialSetupDelegate = m => { m.SetVector(propBuilder.PropertyAttribute.UniformName, inputValue); };
            TestSurfaceBlockIsConstantColor(container, propBuilder.BlockName, block, expectedColor, materialSetupDelegate, errorThreshold: 1);
            yield break;
        }
    }
}
