import { collection as createTreeCollection } from "@zag-js/tree-view";
import { normalizeProps, useMachine } from "@zag-js/preact";
import * as treeView from "@zag-js/tree-view";
import type { ComponentChildren } from "preact";
import { useId, useState } from "preact/hooks";
import type { DirectoryClient, DirectoryListing } from "../files/DirectoryClient.ts";
import "./directory-tree.css";

export interface DirectoryTreeProps {
  listing: DirectoryListing;
  client: DirectoryClient;
  disabled: boolean;
  selectedPath: string | null;
  onSelect(path: string): void;
  onOpen(path: string): void;
}

interface DirectoryNode {
  path: string;
  name: string;
  children?: DirectoryNode[];
}

interface BranchInfo {
  error?: string;
  truncated?: boolean;
}

const hiddenRootPath = "\u0000directory-tree-root";

export function DirectoryTree({
  listing,
  client,
  disabled,
  selectedPath,
  onSelect,
  onOpen,
}: DirectoryTreeProps) {
  const [treeCollection, setTreeCollection] = useState(() => createCollection(listing));
  const [branchInfo, setBranchInfo] = useState<Record<string, BranchInfo>>({});

  const service = useMachine(treeView.machine, {
    id: useId(),
    collection: treeCollection,
    defaultExpandedValue: listing.path ? [listing.path] : [],
    selectedValue: selectedPath ? [selectedPath] : [],
    selectionMode: "single",
    expandOnClick: false,
    loadChildren: async ({ node, signal }) => {
      try {
        const result = await client.list(node.path, undefined, signal);
        if (signal.aborted) throw abortError();
        setBranchInfo((current) => ({
          ...current,
          [node.path]: { truncated: result.truncated },
        }));
        return result.directories.map(toNode);
      } catch (error) {
        if (signal.aborted) throw abortError();
        throw error;
      }
    },
    onLoadChildrenComplete: ({ collection }) =>
      setTreeCollection(collection as typeof treeCollection),
    onLoadChildrenError: ({ nodes }) => {
      const failures = nodes.filter(({ error }) => error.name !== "AbortError");
      if (failures.length === 0) return;
      setBranchInfo((current) => {
        const next = { ...current };
        for (const { node } of failures)
          next[node.path] = { ...next[node.path], error: "Could not load folders." };
        return next;
      });
    },
    onSelectionChange: ({ selectedValue }) => {
      const path = selectedValue[0];
      if (path) onSelect(path);
    },
  });
  const api = treeView.connect(service, normalizeProps);

  const renderNode = (node: DirectoryNode, indexPath: number[]): ComponentChildren => {
    const nodeProps = { node, indexPath };
    const state = api.getNodeState(nodeProps);
    const info = branchInfo[node.path];

    // Keep the focusable row mounted when an unloaded folder becomes an empty leaf.
    if (!state.isBranch) {
      return (
        <div key={node.path} role="none">
          <div {...api.getItemProps(nodeProps)} aria-label={node.name} class="directory-tree-row">
            <FolderIcon />
            <span {...api.getItemTextProps(nodeProps)} class="directory-tree-name">
              {node.name}
            </span>
          </div>
        </div>
      );
    }

    const children = api.collection.getNodeChildren(node);
    const showDetails =
      state.expanded || (!state.loading && Boolean(info?.error || info?.truncated));
    return (
      <div
        key={node.path}
        {...api.getBranchProps(nodeProps)}
        aria-label={node.name}
        class="directory-tree-branch"
      >
        <div {...api.getBranchControlProps(nodeProps)} class="directory-tree-row">
          <span
            {...api.getBranchTriggerProps(nodeProps)}
            tabIndex={-1}
            aria-hidden="true"
            class="directory-tree-trigger"
          >
            <span {...api.getBranchIndicatorProps(nodeProps)}>
              <svg viewBox="0 0 16 16">
                <path d="m5.5 3 5 5-5 5" />
              </svg>
            </span>
          </span>
          <FolderIcon />
          <span {...api.getBranchTextProps(nodeProps)} class="directory-tree-name">
            {node.name}
          </span>
          {state.loading && (
            <span class="directory-tree-status" aria-live="polite">
              Loading…
            </span>
          )}
        </div>
        {showDetails && (
          <div
            {...api.getBranchContentProps(nodeProps)}
            hidden={!state.expanded && !info?.error && !info?.truncated}
            class="directory-tree-content"
          >
            {state.expanded &&
              children.map((child, index) =>
                renderNode(child as DirectoryNode, [...indexPath, index]),
              )}
            {info?.error && !state.loading && (
              <div class="directory-tree-message">
                <span role="status">{info.error}</span>
                <button
                  type="button"
                  onClick={(event) => {
                    event.stopPropagation();
                    api.focus(node.path);
                    api.expand([node.path]);
                  }}
                  onKeyDown={(event) => event.stopPropagation()}
                >
                  Retry
                </button>
              </div>
            )}
            {info?.truncated && (
              <div class="directory-tree-message">
                <span>More folders available</span>
                <button
                  type="button"
                  onClick={(event) => {
                    event.stopPropagation();
                    onOpen(node.path);
                  }}
                  onKeyDown={(event) => event.stopPropagation()}
                >
                  Open folder
                </button>
              </div>
            )}
          </div>
        )}
      </div>
    );
  };

  return (
    <div
      {...api.getRootProps()}
      class="directory-tree"
      inert={disabled}
      aria-disabled={disabled || undefined}
    >
      <span {...api.getLabelProps()} class="directory-tree-label">
        Folders
      </span>
      <div {...api.getTreeProps()}>
        {api.collection
          .getNodeChildren(api.collection.rootNode)
          .map((node, index) => renderNode(node as DirectoryNode, [index]))}
      </div>
    </div>
  );
}

function createCollection(listing: DirectoryListing) {
  const rootChildren: DirectoryNode[] = listing.path
    ? [{ path: listing.path, name: listing.path, children: listing.directories.map(toNode) }]
    : listing.directories.map(toNode);
  const root: DirectoryNode = { path: hiddenRootPath, name: "", children: rootChildren };
  return createTreeCollection<DirectoryNode>({
    rootNode: root,
    nodeToValue: (node) => node.path,
    nodeToString: (node) => node.name,
    nodeToChildren: (node) => node.children ?? [],
    nodeToChildrenCount: (node) => (node.children === undefined ? 1 : undefined),
  });
}

function toNode(directory: { readonly name: string; readonly path: string }): DirectoryNode {
  return { path: directory.path, name: directory.name };
}

function abortError(): Error {
  const error = new Error("Directory listing was aborted.");
  error.name = "AbortError";
  return error;
}

function FolderIcon() {
  return (
    <svg class="directory-tree-folder-icon" aria-hidden="true" viewBox="0 0 16 16">
      <path d="M1.5 3.5h5l1.5 2h6.5v7h-13z" />
    </svg>
  );
}
