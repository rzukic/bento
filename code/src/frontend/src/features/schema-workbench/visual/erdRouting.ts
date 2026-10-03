import type {
  SchemaModelDocument,
  SchemaRelation,
  SchemaTable,
} from "../../../types/api";
import { getRelationEndpoint } from "../schemaWorkbenchHelpers";
import { getTableLayout, type VisualTableLayout } from "../visualSchemaDraft";

type ErdTableNode = {
  height: number;
  table: SchemaTable;
  width: number;
  x: number;
  y: number;
};

type ErdRelationEdge = {
  fromTable: string;
  id: string;
  label: string;
  labelX: number;
  labelY: number;
  path: string;
  relation: SchemaRelation;
  selfReferential: boolean;
  toTable: string;
};

export type ErdRelationHandleSide = "left" | "right";

export type ErdRelationDragState = {
  fromTableName: string;
  side: ErdRelationHandleSide;
  startX: number;
  startY: number;
  targetTableName: string | null;
  x: number;
  y: number;
};

const erdTableWidth = 264;
const erdColumnRowHeight = 30;
const erdTableHeaderHeight = 44;
const erdTableBottomInset = 4;
const erdTableMinHeight = 80;
const erdGridSize = 48;
const erdSnapSize = erdGridSize / 4;
const erdAutoColumnGap = 344;
const erdAutoRowGap = 292;
const erdCanvasPadding = 56;

function getErdTableHeight(table: SchemaTable): number {
  return Math.max(
    erdTableMinHeight,
    erdTableHeaderHeight +
      Math.min(table.columns.length, 8) * erdColumnRowHeight +
      erdTableBottomInset,
  );
}

export function buildErdTableNodes(document: SchemaModelDocument): ErdTableNode[] {
  return document.tables.map((table, index) => {
    const layout = getTableLayout(table);
    const fallbackColumn = index % 3;
    const fallbackRow = Math.floor(index / 3);

    return {
      table,
      width: erdTableWidth,
      height: getErdTableHeight(table),
      x: layout?.x ?? erdCanvasPadding + fallbackColumn * erdAutoColumnGap,
      y: layout?.y ?? erdCanvasPadding + fallbackRow * erdAutoRowGap,
    };
  });
}

export function getErdCanvasSize(nodes: ErdTableNode[]): {
  height: number;
  width: number;
} {
  if (nodes.length === 0) {
    return { width: 720, height: 420 };
  }

  return {
    width:
      Math.max(...nodes.map((node) => node.x + node.width)) + erdCanvasPadding,
    height:
      Math.max(...nodes.map((node) => node.y + node.height)) + erdCanvasPadding,
  };
}

export type { ErdTableNode, ErdRelationEdge };

export function buildErdRelationEdges(
  document: SchemaModelDocument,
  nodes: ErdTableNode[],
): ErdRelationEdge[] {
  const nodeByName = new Map(nodes.map((node) => [node.table.name, node]));

  return document.relations.flatMap<ErdRelationEdge>((relation) => {
    const fromTable = getRelationEndpoint(relation, "from");
    const toTable = getRelationEndpoint(relation, "to");
    const fromNode = nodeByName.get(fromTable);
    const toNode = nodeByName.get(toTable);

    if (!fromNode || !toNode) {
      return [];
    }

    const label = relation.type ?? "relation";

    if (relation.type === "N:M") {
      const junctionTable = getRelationJunctionTableName(relation);
      const junctionNode = junctionTable ? nodeByName.get(junctionTable) : null;

      if (junctionTable && junctionNode && junctionTable !== fromTable && junctionTable !== toTable) {
        return [
          buildErdRelationEdge(
            relation,
            fromTable,
            junctionTable,
            fromNode,
            junctionNode,
            label,
            nodes,
          ),
          buildErdRelationEdge(
            relation,
            junctionTable,
            toTable,
            junctionNode,
            toNode,
            label,
            nodes,
          ),
        ];
      }
    }

    if (fromTable === toTable) {
      return [buildSelfReferentialErdRelationEdge(relation, fromTable, fromNode, label, nodes)];
    }

    return [
      buildErdRelationEdge(relation, fromTable, toTable, fromNode, toNode, label, nodes),
    ];
  });
}

function getRelationJunctionTableName(relation: SchemaRelation): string | null {
  const explicitName = relation.junctionTable?.trim();
  if (explicitName) {
    return explicitName;
  }

  return relation.resolvedJunctionTableName?.trim() || null;
}

export function snapToErdGrid(value: number): number {
  return Math.round(value / erdSnapSize) * erdSnapSize;
}

export function getSuggestedJunctionLayout(
  document: SchemaModelDocument,
  fromTableName: string,
  toTableName: string,
): VisualTableLayout {
  const fromTable = document.tables.find((table) => table.name === fromTableName);
  const toTable = document.tables.find((table) => table.name === toTableName);
  const fromLayout = fromTable ? getTableLayout(fromTable) : null;
  const toLayout = toTable ? getTableLayout(toTable) : null;
  const fallbackLayout = getNextVisualTableLayout(document);

  return {
    x:
      typeof fromLayout?.x === "number" && typeof toLayout?.x === "number"
        ? snapToErdGrid((fromLayout.x + toLayout.x) / 2 + 40)
        : fallbackLayout.x,
    y:
      typeof fromLayout?.y === "number" && typeof toLayout?.y === "number"
        ? snapToErdGrid((fromLayout.y + toLayout.y) / 2 + 120)
        : fallbackLayout.y,
  };
}

const erdRelationChannelMargin = 18;
const erdRelationObstaclePadding = 6;

type ErdRouteSide = "left" | "right";
type ErdRoutePoint = { x: number; y: number };

/** Lines leave and enter a card at its first row (the id). */
function getErdRelationAnchorY(node: ErdTableNode): number {
  return node.y + Math.min(node.height - 12, erdTableHeaderHeight + erdColumnRowHeight / 2);
}

function buildErdRelationEdge(
  relation: SchemaRelation,
  fromTable: string,
  toTable: string,
  fromNode: ErdTableNode,
  toNode: ErdTableNode,
  label: string,
  allNodes: ErdTableNode[],
): ErdRelationEdge {
  const startY = getErdRelationAnchorY(fromNode);
  const endY = getErdRelationAnchorY(toNode);
  const obstacles = allNodes.filter(
    (node) => node !== fromNode && node !== toNode,
  );

  const route = routeOrthogonalRelationPath(
    fromNode,
    toNode,
    startY,
    endY,
    obstacles,
  );

  return {
    relation,
    fromTable,
    id: `${relation.name}:${fromTable}:${toTable}`,
    toTable,
    label,
    selfReferential: false,
    path: route.path,
    labelX: route.labelPoint.x,
    labelY: route.labelPoint.y,
  };
}

function buildSelfReferentialErdRelationEdge(
  relation: SchemaRelation,
  tableName: string,
  node: ErdTableNode,
  label: string,
  allNodes: ErdTableNode[],
): ErdRelationEdge {
  const startX = node.x + node.width;
  const startY = node.y + Math.min(node.height - 36, 56);
  const endY = node.y + Math.min(node.height - 36, 150);
  const obstacles = allNodes.filter((candidate) => candidate !== node);
  const detourX = clearVerticalChannel(
    startX + erdRelationChannelMargin * 4,
    startY,
    endY,
    obstacles,
    erdRelationChannelMargin,
    "right",
  );

  const path = pointsToOrthogonalSvgPath([
    { x: startX, y: startY },
    { x: detourX, y: startY },
    { x: detourX, y: endY },
    { x: startX, y: endY },
  ]);

  return {
    relation,
    fromTable: tableName,
    id: `${relation.name}:${tableName}:${tableName}:self`,
    toTable: tableName,
    label,
    selfReferential: true,
    path,
    labelX: detourX,
    labelY: (startY + endY) / 2,
  };
}

function nodesOverlapHorizontally(a: ErdTableNode, b: ErdTableNode): boolean {
  return a.x < b.x + b.width && b.x < a.x + a.width;
}

function sideX(node: ErdTableNode, side: ErdRouteSide): number {
  return side === "right" ? node.x + node.width : node.x;
}

/**
 * Picks the sides a relation leaves and enters its tables on.
 *
 * Side-by-side tables connect across the gap between them. Tables that
 * overlap horizontally (stacked above each other) cannot do that: a channel
 * between "right of A" and "left of B" would have to cross one of the cards.
 * They leave and enter on the same side instead, using whichever side has the
 * less obstructed vertical channel.
 */
function chooseErdRelationSides(
  fromNode: ErdTableNode,
  toNode: ErdTableNode,
  startY: number,
  endY: number,
  obstacles: ErdTableNode[],
): { startSide: ErdRouteSide; endSide: ErdRouteSide } {
  const margin = erdRelationChannelMargin;

  if (!nodesOverlapHorizontally(fromNode, toNode)) {
    const exitsRight = fromNode.x + fromNode.width / 2 <= toNode.x + toNode.width / 2;
    return exitsRight
      ? { startSide: "right", endSide: "left" }
      : { startSide: "left", endSide: "right" };
  }

  const rightIdeal = Math.max(sideX(fromNode, "right"), sideX(toNode, "right")) + margin * 2;
  const leftIdeal = Math.min(fromNode.x, toNode.x) - margin * 2;
  const rightActual = clearVerticalChannel(rightIdeal, startY, endY, obstacles, margin, "right");
  const leftActual = clearVerticalChannel(leftIdeal, startY, endY, obstacles, margin, "left");
  const side: ErdRouteSide =
    rightActual - rightIdeal <= leftIdeal - leftActual ? "right" : "left";

  return { startSide: side, endSide: side };
}

function routeOrthogonalRelationPath(
  fromNode: ErdTableNode,
  toNode: ErdTableNode,
  startY: number,
  endY: number,
  obstacles: ErdTableNode[],
): { path: string; labelPoint: ErdRoutePoint } {
  const margin = erdRelationChannelMargin;
  const endpoints = [fromNode, toNode];
  let { startSide, endSide } = chooseErdRelationSides(fromNode, toNode, startY, endY, obstacles);

  const computeMidX = (): number => {
    const exitX = sideX(fromNode, startSide) + (startSide === "right" ? margin : -margin);
    const entryX = sideX(toNode, endSide) + (endSide === "right" ? margin : -margin);

    let midX: number;
    let preferDirection: ErdRouteSide | null = null;
    if (startSide === endSide) {
      midX =
        startSide === "right"
          ? Math.max(exitX, entryX) + margin
          : Math.min(exitX, entryX) - margin;
      preferDirection = startSide;
    } else {
      midX = (exitX + entryX) / 2;
    }

    midX = clampMidXOutsideEndpoints(midX, startY, endY, endpoints, margin, preferDirection);
    midX = clearVerticalChannel(midX, startY, endY, obstacles, margin, preferDirection);
    midX = clampMidXOutsideEndpoints(midX, startY, endY, endpoints, margin, preferDirection);
    return midX;
  };

  let midX = computeMidX();

  // Clamping and obstacle avoidance can push the vertical channel to the far
  // side of a table. The final horizontal segment must then enter that table
  // from the side the channel is on, otherwise it runs across the card.
  const resolveSide = (node: ErdTableNode, fallback: ErdRouteSide): ErdRouteSide =>
    midX >= node.x + node.width ? "right" : midX <= node.x ? "left" : fallback;
  const resolvedStartSide = resolveSide(fromNode, startSide);
  const resolvedEndSide = resolveSide(toNode, endSide);
  if (resolvedStartSide !== startSide || resolvedEndSide !== endSide) {
    startSide = resolvedStartSide;
    endSide = resolvedEndSide;
    midX = computeMidX();
  }

  const start: ErdRoutePoint = { x: sideX(fromNode, startSide), y: startY };
  const end: ErdRoutePoint = { x: sideX(toNode, endSide), y: endY };
  const exitX = start.x + (startSide === "right" ? margin : -margin);
  const entryX = end.x + (endSide === "right" ? margin : -margin);

  let waypoints: ErdRoutePoint[] = [
    start,
    { x: exitX, y: start.y },
    { x: midX, y: start.y },
    { x: midX, y: end.y },
    { x: entryX, y: end.y },
    end,
  ];

  waypoints = detourHorizontalSegments(waypoints, obstacles, margin);

  return {
    path: pointsToOrthogonalSvgPath(waypoints),
    labelPoint: { x: midX, y: (start.y + end.y) / 2 },
  };
}

function clampMidXOutsideEndpoints(
  midX: number,
  y1: number,
  y2: number,
  endpoints: ErdTableNode[],
  margin: number,
  preferDirection: ErdRouteSide | null,
): number {
  const yMin = Math.min(y1, y2);
  const yMax = Math.max(y1, y2);

  for (let i = 0; i < 4; i += 1) {
    const blocking = endpoints.find((node) => {
      const top = node.y;
      const bottom = node.y + node.height;
      const left = node.x;
      const right = node.x + node.width;
      const verticalOverlap = yMin < bottom && yMax > top;
      return verticalOverlap && midX > left && midX < right;
    });

    if (!blocking) {
      return midX;
    }

    const moveRight = blocking.x + blocking.width + margin;
    const moveLeft = blocking.x - margin;

    if (preferDirection === "right") {
      midX = moveRight;
    } else if (preferDirection === "left") {
      midX = moveLeft;
    } else {
      const distRight = Math.abs(moveRight - midX);
      const distLeft = Math.abs(midX - moveLeft);
      midX = distRight <= distLeft ? moveRight : moveLeft;
    }
  }

  return midX;
}

function clearVerticalChannel(
  initialX: number,
  y1: number,
  y2: number,
  obstacles: ErdTableNode[],
  margin: number,
  preferDirection: ErdRouteSide | null,
): number {
  let x = initialX;
  const yMin = Math.min(y1, y2);
  const yMax = Math.max(y1, y2);

  for (let i = 0; i < 8; i += 1) {
    const blocking = obstacles.find((obs) => {
      const left = obs.x - erdRelationObstaclePadding;
      const right = obs.x + obs.width + erdRelationObstaclePadding;
      const top = obs.y - erdRelationObstaclePadding;
      const bottom = obs.y + obs.height + erdRelationObstaclePadding;
      return (
        x >= left && x <= right && yMin <= bottom && yMax >= top
      );
    });

    if (!blocking) {
      return x;
    }

    const moveRight = blocking.x + blocking.width + margin;
    const moveLeft = blocking.x - margin;

    if (preferDirection === "right") {
      x = moveRight;
    } else if (preferDirection === "left") {
      x = moveLeft;
    } else {
      const distRight = Math.abs(moveRight - x);
      const distLeft = Math.abs(x - moveLeft);
      x = distRight <= distLeft ? moveRight : moveLeft;
    }
  }

  return x;
}

function detourHorizontalSegments(
  points: ErdRoutePoint[],
  obstacles: ErdTableNode[],
  margin: number,
): ErdRoutePoint[] {
  const result: ErdRoutePoint[] = [];

  for (let i = 0; i < points.length; i += 1) {
    const current = points[i];
    result.push(current);
    const next = points[i + 1];
    if (!next) {
      continue;
    }

    if (current.y !== next.y) {
      continue;
    }

    const segmentY = current.y;
    const segmentMinX = Math.min(current.x, next.x);
    const segmentMaxX = Math.max(current.x, next.x);

    const blocking = obstacles.find((obs) => {
      const top = obs.y - erdRelationObstaclePadding;
      const bottom = obs.y + obs.height + erdRelationObstaclePadding;
      const left = obs.x - erdRelationObstaclePadding;
      const right = obs.x + obs.width + erdRelationObstaclePadding;
      return (
        segmentY >= top &&
        segmentY <= bottom &&
        segmentMaxX >= left &&
        segmentMinX <= right
      );
    });

    if (!blocking) {
      continue;
    }

    const detourAbove = blocking.y - margin;
    const detourBelow = blocking.y + blocking.height + margin;
    const detourY =
      Math.abs(segmentY - detourAbove) <= Math.abs(segmentY - detourBelow)
        ? detourAbove
        : detourBelow;

    const detourEntryX = current.x;
    const detourExitX = next.x;
    result.push({ x: detourEntryX, y: detourY });
    result.push({ x: detourExitX, y: detourY });
  }

  return collapseCollinearPoints(result);
}

function collapseCollinearPoints(points: ErdRoutePoint[]): ErdRoutePoint[] {
  const result: ErdRoutePoint[] = [];
  for (const point of points) {
    const last = result[result.length - 1];
    if (last && last.x === point.x && last.y === point.y) {
      continue;
    }
    result.push(point);
  }
  return result;
}

function pointsToOrthogonalSvgPath(points: ErdRoutePoint[]): string {
  const collapsed = collapseCollinearPoints(points);
  if (collapsed.length === 0) {
    return "";
  }
  const [first, ...rest] = collapsed;
  return [`M ${first.x} ${first.y}`, ...rest.map((p) => `L ${p.x} ${p.y}`)].join(
    " ",
  );
}

export function getErdRelationHandlePoint(
  node: ErdTableNode,
  side: ErdRelationHandleSide,
) {
  return {
    x: side === "right" ? node.x + node.width : node.x,
    y: node.y + node.height / 2,
  };
}

export function getErdDragPath(drag: ErdRelationDragState): string {
  const direction = drag.side === "right" ? 1 : -1;
  const exitX = drag.startX + erdRelationChannelMargin * direction;
  const midX =
    Math.abs(drag.x - exitX) < erdRelationChannelMargin
      ? exitX
      : (exitX + drag.x) / 2;

  return pointsToOrthogonalSvgPath([
    { x: drag.startX, y: drag.startY },
    { x: exitX, y: drag.startY },
    { x: midX, y: drag.startY },
    { x: midX, y: drag.y },
    { x: drag.x, y: drag.y },
  ]);
}

export function getErdDropTargetTableName(
  nodes: ErdTableNode[],
  fromTableName: string,
  point: { x: number; y: number },
): string | null {
  const target = nodes.find(
    (node) =>
      node.table.name !== fromTableName &&
      point.x >= node.x &&
      point.x <= node.x + node.width &&
      point.y >= node.y &&
      point.y <= node.y + node.height,
  );

  return target?.table.name ?? null;
}

/**
 * A free spot for a new table: the first cell of a loose grid (left to
 * right, top to bottom) whose card would not touch any existing table.
 */
export function getNextVisualTableLayout(document: SchemaModelDocument) {
  const nodes = buildErdTableNodes(document);
  const gap = 48;
  const newHeight = erdTableMinHeight + erdColumnRowHeight;
  const fits = (x: number, y: number) =>
    nodes.every(
      (node) =>
        x + erdTableWidth + gap <= node.x ||
        x >= node.x + node.width + gap ||
        y + newHeight + gap <= node.y ||
        y >= node.y + node.height + gap,
    );
  for (let row = 0; row < 40; row += 1) {
    for (let column = 0; column < 4; column += 1) {
      const x = snapToErdGrid(80 + column * (erdTableWidth + 72));
      const y = snapToErdGrid(80 + row * 120);
      if (fits(x, y)) return { x, y };
    }
  }
  const bottom = Math.max(80, ...nodes.map((node) => node.y + node.height));
  return { x: 80, y: snapToErdGrid(bottom + gap) };
}

/** Bounding box of all tables, for fitting the view. */
export function getErdContentBounds(nodes: ErdTableNode[]) {
  if (nodes.length === 0) return null;
  const left = Math.min(...nodes.map((node) => node.x));
  const top = Math.min(...nodes.map((node) => node.y));
  const right = Math.max(...nodes.map((node) => node.x + node.width));
  const bottom = Math.max(...nodes.map((node) => node.y + node.height));
  return { x: left, y: top, width: right - left, height: bottom - top };
}

/**
 * The routed (orthogonal) path drawn with rounded bends, so relation lines
 * read as soft curves. Hit-testing and tests keep using the plain path.
 */
export function smoothErdPath(path: string, radius = 18): string {
  const points = [...path.matchAll(/[ML] (-?[\d.]+) (-?[\d.]+)/g)].map((match) => ({
    x: Number(match[1]),
    y: Number(match[2]),
  }));
  if (points.length < 3) return path;
  const parts = [`M ${points[0]!.x} ${points[0]!.y}`];
  for (let index = 1; index < points.length - 1; index += 1) {
    const previous = points[index - 1]!;
    const corner = points[index]!;
    const next = points[index + 1]!;
    const inLength = Math.hypot(corner.x - previous.x, corner.y - previous.y);
    const outLength = Math.hypot(next.x - corner.x, next.y - corner.y);
    const r = Math.min(radius, inLength / 2, outLength / 2);
    const enter = {
      x: corner.x - ((corner.x - previous.x) / (inLength || 1)) * r,
      y: corner.y - ((corner.y - previous.y) / (inLength || 1)) * r,
    };
    const leave = {
      x: corner.x + ((next.x - corner.x) / (outLength || 1)) * r,
      y: corner.y + ((next.y - corner.y) / (outLength || 1)) * r,
    };
    parts.push(`L ${enter.x} ${enter.y}`, `Q ${corner.x} ${corner.y} ${leave.x} ${leave.y}`);
  }
  const last = points[points.length - 1]!;
  parts.push(`L ${last.x} ${last.y}`);
  return parts.join(" ");
}
