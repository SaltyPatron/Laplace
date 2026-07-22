export interface MeshLink {
  id: string;
  label: string;
  relation: string;
  hub_type?: string | null;
  eff_mu?: number | null;
  witnesses: number;
}

export interface MeshResponse {
  id: string;
  label: string;
  hub_type?: string | null;
  belongs_to: MeshLink[];
  roster: MeshLink[];
}
