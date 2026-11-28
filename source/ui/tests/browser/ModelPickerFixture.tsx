import { render } from "preact";
import { useMemo, useState } from "preact/hooks";
import "../../base.css";
import type { ModelFavorite } from "../../preferences/PreferencesClient.ts";
import { ModelFavoritesProvider } from "../../providers/models/ModelFavorites.tsx";
import { ModelPicker } from "../../providers/models/ModelPicker.tsx";

const models = [
  {
    key: "one/model-a",
    connectionId: "one",
    modelId: "model-a",
    modelLabel: "Model A",
    providerLabel: "Provider One",
    inputPrice: 1,
    outputPrice: 5,
    priceSource: "Fixture rates",
    priceCapturedAt: "2026-11-28T00:00:00Z",
    supportsImages: true,
  },
  {
    key: "one/claude-haiku-4.5-20261128",
    connectionId: "one",
    modelId: "claude-haiku-4.5-20261128",
    modelLabel: "Claude Haiku 4.5",
    providerLabel: "Provider One",
    inputPrice: 0.8,
    outputPrice: 4,
    priceSource: "Fixture rates",
    priceCapturedAt: "2026-11-28T00:00:00Z",
    supportsImages: true,
  },
  {
    key: "two/model-a",
    connectionId: "two",
    modelId: "model-a",
    modelLabel: "Model A",
    providerLabel: "Provider Two",
    inputPrice: null,
    outputPrice: null,
    priceSource: null,
    priceCapturedAt: null,
    supportsImages: false,
  },
  {
    key: "two/model-c",
    connectionId: "two",
    modelId: "model-c",
    modelLabel: "Model C",
    providerLabel: "Provider Two",
    inputPrice: 3,
    outputPrice: 12,
    priceSource: "Fixture rates",
    priceCapturedAt: "2026-11-28T00:00:00Z",
    supportsImages: false,
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
  const [favorites, setFavorites] = useState<readonly ModelFavorite[]>([]);
  const model = models.find(({ key }) => key === modelKey);
  const preferences = useMemo(
    () => ({
      loaded: true,
      pending: false,
      pendingFor: null,
      preferences: { modelFavorites: favorites },
      saveModelFavorites: async (next: readonly ModelFavorite[]) => {
        setFavorites(next);
        return true;
      },
    }),
    [favorites],
  );
  return (
    <main>
      <ModelFavoritesProvider preferences={preferences}>
        <ModelPicker
          models={models}
          modelKey={modelKey}
          modelLabel={model?.modelLabel ?? "Choose model"}
          onModelChange={setModelKey}
          disabled={false}
        />
      </ModelFavoritesProvider>
      <output aria-label="Selected model">{modelKey}</output>
      <output aria-label="Favorite models">
        {favorites.map(({ connectionId, modelId }) => `${connectionId}/${modelId}`).join(",")}
      </output>
    </main>
  );
}

render(<Fixture />, document.body);
