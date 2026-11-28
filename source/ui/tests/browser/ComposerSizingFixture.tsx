import type { RefObject } from "preact";
import { render } from "preact";
import { useState } from "preact/hooks";
import { Composer } from "../../chat/composer/Composer.tsx";
import "../../chat/composer/composer.css";

const root = document.createElement("main");
document.body.append(root);
document.head.insertAdjacentHTML(
  "beforeend",
  `<style>
    * { box-sizing: border-box; }
    body { margin: 0; font: 14px sans-serif; }
    main { height: 600px; width: 100%; }
  </style>`,
);

const inputRef: RefObject<HTMLTextAreaElement | null> = { current: null };

function Fixture() {
  const [text, setText] = useState("");
  return (
    <Composer
      input={inputRef}
      text={text}
      hasImages={false}
      imagePreviews={null}
      onTextChange={setText}
      models={[]}
      modelKey={null}
      modelLabel="Model"
      onModelChange={() => {}}
      hasProviders={true}
      loadingModels={false}
      refreshingModels={false}
      modelsError={null}
      onRetryModels={() => {}}
      onSetUpProvider={() => {}}
      reasoningEffort={null}
      reasoningOptions={[]}
      onReasoningChange={() => {}}
      selectionLocked={false}
      configurationPending={false}
      canSend={false}
      canContinue={false}
      continuationAvailable={false}
      working={false}
      stopping={false}
      onSend={() => {}}
      onContinue={() => {}}
      onStop={() => {}}
    />
  );
}

render(<Fixture />, root);
