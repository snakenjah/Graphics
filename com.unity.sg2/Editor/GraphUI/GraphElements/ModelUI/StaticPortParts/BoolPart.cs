using Unity.GraphToolsFoundation.Editor;
using UnityEditor.ShaderGraph.GraphDelta;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityEditor.ShaderGraph.GraphUI
{
    class BoolPart : SingleFieldPart<Toggle, bool>
    {
        protected override string UXMLTemplateName => "StaticPortParts/BoolPart";
        protected override string FieldName => "sg-bool-field";

        public BoolPart(
            string name,
            GraphElementModel model,
            ModelView ownerElement,
            string parentClassName,
            string portName,
            string portDisplayName
        ) : base(name, model, ownerElement, parentClassName, portName, portDisplayName) { }

        protected override void OnFieldValueChanged(ChangeEvent<bool> change)
        {
            if (m_Model is not SGNodeModel graphDataNodeModel) return;
            m_OwnerElement.RootView.Dispatch(
                new SetGraphTypeValueCommand(graphDataNodeModel,
                    m_PortName,
                    GraphType.Length.One,
                    GraphType.Height.One,
                    change.newValue ? 1f : 0f
                )
            );
        }

        protected override void UpdatePartFromPortReader(PortHandler reader)
        {
            if (!reader.GetTypeField().GetField("c0", out float value)) value = 0;
            bool v = !Mathf.Approximately(value, 0F);
            m_Field.SetValueWithoutNotify(v);
        }
    }
}
