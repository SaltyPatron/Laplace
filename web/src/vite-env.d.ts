/// <reference types="vite/client" />

declare module '*.module.css' {
  const classes: Record<string, string>;
  export default classes;
}

declare module 'd3-force-3d' {
  interface ManyBodyForce {
    strength(v: number | ((node: unknown, i: number, nodes: unknown[]) => number)): ManyBodyForce;
    distanceMax(v: number): ManyBodyForce;
  }
  interface RadialForce {
    strength(v: number): RadialForce;
  }
  interface CollideForce {
    strength(v: number): CollideForce;
  }
  export function forceManyBody(): ManyBodyForce;
  export function forceRadial(
    radius: number | ((node: unknown, i: number, nodes: unknown[]) => number),
    x?: number,
    y?: number,
    z?: number,
  ): RadialForce;
  export function forceCollide(radius?: number | ((node: unknown) => number)): CollideForce;
}
