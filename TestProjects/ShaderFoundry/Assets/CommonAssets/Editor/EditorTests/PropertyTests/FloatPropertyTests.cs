using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityEditor.ShaderFoundry.UnitTests
{
    class FloatPropertyTests : BlockTestRenderer
    {
        ScalarPropertyBlockBuilder BuildWithoutNameOverrides(string defaultValue)
        {
            var propBuilder = new ScalarPropertyBlockBuilder
            {
                PropertyAttribute = new PropertyAttributeData { DefaultValue = defaultValue },
                FieldName = "FloatField",
            };
            return propBuilder;
        }

        ScalarPropertyBlockBuilder BuildWithNameOverrides(string defaultValue)
        {
            var propBuilder = new ScalarPropertyBlockBuilder
            {
                PropertyAttribute = new PropertyAttributeData
                {
                    UniformName = "_Value",
                    DisplayName = "Value",
                    DefaultValue = defaultValue
                },
                FieldName = "FloatField",
            };
            return propBuilder;
        }

        [UnityTest]
        public IEnumerator FloatProperty_DefaultPropertyValueUsed_IsExpectedColor()
        {
            var expectedColor = new Color(1, 0, 0, 0);

            var container = CreateContainer();
            var propBuilder = BuildWithoutNameOverrides("1");
            var block = propBuilder.Build(container, container._float);

            TestSurfaceBlockIsConstantColor(container, propBuilder.BlockName, block, expectedColor);
            yield break;
        }

        [UnityTest]
        public IEnumerator FloatProperty_MaterialColorSet_IsExpectedColor()
        {
            var inputValue = 0.1f;
            var expectedColor = new Color(inputValue, 0, 0, 0);

            var container = CreateContainer();
            var propBuilder = BuildWithNameOverrides("1");
            var block = propBuilder.Build(container, container._float);

            SetupMaterialDelegate materialSetupDelegate = m => { m.SetFloat(propBuilder.PropertyAttribute.UniformName, inputValue); };
            TestSurfaceBlockIsConstantColor(container, propBuilder.BlockName, block, expectedColor, materialSetupDelegate);
            yield break;
        }

        [Test]
        public void FloatProperty_NoPropertyNameOverrides_ShaderPropertiesAreValid()
        {
            float expectedDefaultValue = 0.0f;

            var container = CreateContainer();
            var propBuilder = BuildWithoutNameOverrides(expectedDefaultValue.ToString());
            var block = propBuilder.Build(container, container._float);

            var shader = BuildSimpleSurfaceBlockShaderObject(container, propBuilder.BlockName, block);

            var propIndex = shader.FindPropertyIndex(propBuilder.FieldName);
            Assert.AreNotEqual(-1, propIndex);
            PropertyValidationHelpers.ValidateFloatProperty(shader, propIndex, propBuilder, expectedDefaultValue);

            UnityEngine.Object.DestroyImmediate(shader);
        }

        [Test]
        public void FloatProperty_RangeProperty_VerifyIsRange()
        {
            float expectedDefaultValue = 1.0f;
            Vector2 expectedRangeLimits = new Vector2(0, 5);

            var container = CreateContainer();
            var propBuilder = BuildWithNameOverrides(expectedDefaultValue.ToString());
            var rangeAttribute = new RangeAttribute() { Min = expectedRangeLimits.x, Max = expectedRangeLimits.y };
            var attributes = new List<ShaderAttribute> { rangeAttribute.Build(container) };
            var block = propBuilder.BuildWithAttributeOverrides(container, container._float, attributes);

            var shader = BuildSimpleSurfaceBlockShaderObject(container, propBuilder.BlockName, block);

            var propIndex = shader.FindPropertyIndex(propBuilder.PropertyAttribute.UniformName);
            Assert.AreNotEqual(-1, propIndex);
            PropertyValidationHelpers.ValidateRangeProperty(shader, propIndex, propBuilder, expectedDefaultValue, expectedRangeLimits);

            UnityEngine.Object.DestroyImmediate(shader);
        }
    }
}
