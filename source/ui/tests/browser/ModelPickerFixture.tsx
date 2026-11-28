import { render } from "preact";
import { useState } from "preact/hooks";
import "../../base.css";
import { ModelPicker } from "../../providers/models/ModelPicker.tsx";

const models = [
  {
    key: "one/model-a",
    modelId: "model-a",
    modelLabel: "Model A",
    providerLabel: "Provider One",
  },
  {
    key: "one/claude-haiku-4.5-20261128",
    modelId: "claude-haiku-4.5-20261128",
    modelLabel: "Claude Haiku 4.5",
    providerLabel: "Provider One",
  },
  {
    key: "two/model-a",
    modelId: "model-a",
    modelLabel: "Model A",
    providerLabel: "Provider Two",
  },
  {
    key: "two/model-c",
    modelId: "model-c",
    modelLabel: "Model C",
    providerLabel: "Provider Two",
  },
];

document.body.insertAdjacentHTML(
  "afterbegin",
  `<style>
    body { margin: 40px; }
  </style>`,
);

function Fixture() {
  const [modelKey, setModelKey] = useState<string | null>(null);
  const model = models.find(({ key }) => key === modelKey);
  return (
    <main>
      <ModelPicker
        models={models}
        modelKey={modelKey}
        modelLabel={model?.modelLabel ?? "Choose model"}
        onModelChange={setModelKey}
        disabled={false}
      />
      <output aria-label="Selected model">{modelKey}</output>
    </main>
  );
}

render(<Fixture />, document.body);
