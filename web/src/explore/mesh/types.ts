export interface MeshLink {
  id: string;
  label: string | null;
  relation: string;
  hub_type?: string | null;
  eff_mu?: number | null;
  witnesses: number;
}

export interface MeshResponse {
  id: string;
  label: string | null;
  hub_type?: string | null;
  belongs_to: MeshLink[];
  roster: MeshLink[];
}
