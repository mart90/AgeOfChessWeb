/**
 * Serialization helpers for MoveNode trees.
 * Used to persist analysis state in localStorage and transfer trees between pages.
 */
import { MoveNode } from './moveTree.js';

/**
 * Serialize a MoveNode tree to a plain JSON-serializable object.
 * Strips circular parent references.
 */
export function serializeTree(root) {
  function ser(node) {
    return {
      san:             node.san,
      boardState:      node.boardState,
      isGameMainline:  node.isGameMainline,
      children:        node.children.map(ser),
    };
  }
  return ser(root);
}

/**
 * Reconstruct a MoveNode tree from serialized form.
 * Restores parent references.
 */
export function deserializeTree(data, parent = null) {
  const node = new MoveNode(data.san, data.boardState, parent);
  node.isGameMainline = data.isGameMainline ?? false;
  node.children = (data.children ?? []).map(c => deserializeTree(c, node));
  return node;
}

/**
 * Returns an array of child indices tracing the path from root to target.
 * Returns [] if root === target or target not found.
 */
export function getNodePath(root, target) {
  function search(node, tgt, path) {
    if (node === tgt) return path;
    for (let i = 0; i < node.children.length; i++) {
      const r = search(node.children[i], tgt, [...path, i]);
      if (r !== null) return r;
    }
    return null;
  }
  return search(root, target, []) ?? [];
}

/**
 * Follow a path of child indices from root and return the node found.
 * Stops early (returns last valid node) if a path index is out of range.
 */
export function nodeAtPath(root, path) {
  let node = root;
  for (const idx of path) {
    if (!node.children[idx]) break;
    node = node.children[idx];
  }
  return node;
}
