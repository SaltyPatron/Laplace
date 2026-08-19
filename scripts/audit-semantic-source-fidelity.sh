#!/usr/bin/env bash

set -euo pipefail

DATA_ROOT="${LAPLACE_DATA_ROOT:-/vault/Data}"

section() {
  echo
  echo "======== $* ========"
}

present() {
  local path="$1"
  if [[ -e "$path" ]]; then
    echo "PASS  $path"
  else
    echo "MISS  $path"
  fi
}

count_matches() {
  local pattern="$1"
  local path="$2"
  rg -I -o "$pattern" "$path" --glob '*.xml' 2>/dev/null | wc -l
}

section "semantic source artifacts: $DATA_ROOT"
present "$DATA_ROOT/CILI/ili.ttl"
present "$DATA_ROOT/CILI/ili-map-pwn30.tab"
present "$DATA_ROOT/Wordnet/WordNet-3.0/dict/data.noun"
present "$DATA_ROOT/Wordnet/WordNet-3.0/dict/index.sense"
present "$DATA_ROOT/OMW/index.toml"
present "$DATA_ROOT/VerbNet/verbnet-master/verbnet3.4"
present "$DATA_ROOT/PropBank/propbank-frames-main/frames"
present "$DATA_ROOT/FrameNet/framenet_v17/frame"
present "$DATA_ROOT/SemLink/semlink-master/instances/pb-vn2.json"
present "$DATA_ROOT/PredicateMatrix.v1.3/PredicateMatrix.v1.3.txt"
present "$DATA_ROOT/MapNet-0.1/mapping_frame_synsets.txt"
present "$DATA_ROOT/WordFrameNet/WFN/WordFrameNet"

section "direct WN-LMF artifacts"
wn_lmf_count="$({ find "$DATA_ROOT/OMW" "$DATA_ROOT/Wordnet" -maxdepth 5 -type f \
  \( -iname '*wordnet*.xml' -o -iname '*wn-lmf*.xml' \) -print 2>/dev/null || true; } | wc -l)"
echo "WN-LMF candidates=$wn_lmf_count"

section "CILI and OMW packaging"
if [[ -f "$DATA_ROOT/CILI/ili.ttl" ]]; then
  echo "CILI_concepts=$(rg -c $'\ta\t<Concept>' "$DATA_ROOT/CILI/ili.ttl")"
  echo "CILI_instances=$(rg -c $'\ta\t<Instance>' "$DATA_ROOT/CILI/ili.ttl")"
fi
if [[ -d "$DATA_ROOT/OMW/wns" ]]; then
  echo "OMW_legacy_tab_files=$(find "$DATA_ROOT/OMW/wns" -type f -name '*.tab' | wc -l)"
  echo "OMW_declared_packages=$(rg -c '^\[packages\.' "$DATA_ROOT/OMW/index.toml")"
fi

section "Wiktionary admission"
wiktionary_json=""
for candidate in \
  "$DATA_ROOT/Wiktionary/raw-wiktextract-data.jsonl" \
  "$DATA_ROOT/Wiktionary/kaikki.org-dictionary-English.jsonl"
do
  if [[ -f "$candidate" ]]; then
    wiktionary_json="$candidate"
    break
  fi
done
if [[ -n "$wiktionary_json" ]]; then
  echo "PASS  supported JSONL=$wiktionary_json"
else
  echo "BLOCK no supported Wiktionary JSONL; raw MediaWiki XML is not decomposer input"
  find "$DATA_ROOT/Wiktionary" -maxdepth 2 -type f -printf '      %p\n' 2>/dev/null || true
fi

wordnet_dict="$DATA_ROOT/Wordnet/WordNet-3.0/dict"
if [[ -f "$wordnet_dict/index.sense" ]]; then
  section "WordNet sense-key identity"
  awk '
    {
      full_rows++
      split($1, a, "%")
      split(a[2], b, ":")
      normalized = a[1] "%" b[1] ":" b[2] ":" b[3]
      count[normalized]++
    }
    END {
      unique = collision_keys = collision_rows = 0
      for (key in count) {
        unique++
        if (count[key] > 1) {
          collision_keys++
          collision_rows += count[key]
        }
      }
      printf "full_rows=%d normalized_unique=%d lost=%d collision_keys=%d collision_rows=%d\n",
        full_rows, unique, full_rows - unique, collision_keys, collision_rows
    }
  ' "$wordnet_dict/index.sense"

  awk '
    function hex(s, i, c, n, p) {
      n = 0
      for (i = 1; i <= length(s); i++) {
        c = substr(s, i, 1)
        p = index("0123456789abcdef", tolower(c)) - 1
        if (p < 0) return -1
        n = n * 16 + p
      }
      return n
    }
    /^[0-9]/ {
      words = hex($4)
      pointer_index = 5 + 2 * words
      pointer_count = $(pointer_index) + 0
      for (j = 0; j < pointer_count; j++) {
        base = pointer_index + 1 + 4 * j
        source_target = $(base + 3)
        pointers++
        if (source_target != "0000") lexical++
      }
    }
    END { printf "pointers=%d lexical_word_to_word=%d\n", pointers, lexical }
  ' "$wordnet_dict"/data.{noun,verb,adj,adv}
fi

frame_root="$DATA_ROOT/FrameNet/framenet_v17"
if [[ -d "$frame_root/frame" ]]; then
  section "FrameNet scope and annotation"
  fe_declarations="$(count_matches '<FE[ >]' "$frame_root/frame")"
  fe_names="$(rg -I -o '<FE [^>]*' "$frame_root/frame" --glob '*.xml' \
    | sed -nE 's/.*name="([^"]+)".*/\1/p' | sort -u | wc -l)"
  echo "FE_declarations=$fe_declarations unique_name_only_anchors=$fe_names"
  echo "FE_core_sets=$(count_matches '<FEcoreSet[ >]' "$frame_root/frame")"
  echo "frame_semtype_refs=$(count_matches '<semType[ >]' "$frame_root/frame")"
  echo "LU_annotation_labels=$(count_matches '<label[ >]' "$frame_root/lu")"
  echo "fulltext_annotation_labels=$(count_matches '<label[ >]' "$frame_root/fulltext")"
fi

current_framenet_frames() {
  rg -I -m1 '<frame ' "$frame_root/frame" --glob '*.xml' \
    | sed -nE 's/.*name="([^"]+)".*/\1/p' | sort -u
}

current_framenet_lus() {
  rg -I -m1 '<lexUnit ' "$frame_root/lu" --glob '*.xml' \
    | sed -nE 's/.*name="([^"]+)".*frame="([^"]+)".*/\2\t\1/p' | sort -u
}

wfn_lu_keys() {
  local path="$1"
  perl -ane '
    if (/^Frame:\s*(.+?)\s*$/) { $frame = $1; next }
    next unless $frame;
    for ($i = 1; $i < @F; $i++) {
      next unless $F[$i] =~ /^\d+-[nvasr]$/;
      $head = $F[$i - 1];
      if ($head =~ /^(.+)\|([^|]+)$/) {
        ($lemma, $pos) = ($1, $2);
      } else {
        $pos = $head;
        $lemma = $i - 1 == 1 ? $F[0] : join("_", @F[0 .. $i - 2]);
      }
      $pos = "adv" if $pos eq "r";
      print "$frame\t$lemma.$pos\n";
      last;
    }
  ' "$path" | sort -u
}

mapnet_frame="$DATA_ROOT/MapNet-0.1/mapping_frame_synsets.txt"
mapnet_lu="$DATA_ROOT/MapNet-0.1/mapping_lus_synsets.txt"
wfn="$DATA_ROOT/WordFrameNet/WFN/WordFrameNet"
xwfn="$DATA_ROOT/WordFrameNet/XWFN/eXtendedWFN"
if [[ -d "$frame_root/lu" && -f "$mapnet_frame" && -f "$mapnet_lu" ]]; then
  section "legacy FrameNet bridge admission"
  mapnet_frames="$(cut -f1 "$mapnet_frame" | sort -u | wc -l)"
  mapnet_frames_resolved="$(comm -12 \
    <(cut -f1 "$mapnet_frame" | sort -u) <(current_framenet_frames) | wc -l)"
  mapnet_lus="$(cut -f1,2 "$mapnet_lu" | sort -u | wc -l)"
  mapnet_lus_resolved="$(comm -12 \
    <(cut -f1,2 "$mapnet_lu" | sort -u) <(current_framenet_lus) | wc -l)"
  echo "MapNet_frames=$mapnet_frames FrameNet17_name_matches=$mapnet_frames_resolved"
  echo "MapNet_LUs=$mapnet_lus FrameNet17_exact_matches=$mapnet_lus_resolved"

  if [[ -f "$wfn" ]]; then
    wfn_lus="$(wfn_lu_keys "$wfn" | wc -l)"
    wfn_resolved="$(comm -12 <(wfn_lu_keys "$wfn") <(current_framenet_lus) | wc -l)"
    echo "WFN_LUs=$wfn_lus FrameNet17_exact_matches=$wfn_resolved"
  fi
  if [[ -f "$xwfn" ]]; then
    xwfn_lus="$(wfn_lu_keys "$xwfn" | wc -l)"
    xwfn_resolved="$(comm -12 <(wfn_lu_keys "$xwfn") <(current_framenet_lus) | wc -l)"
    echo "XWFN_LUs=$xwfn_lus FrameNet17_exact_matches=$xwfn_resolved"
  fi
fi

verbnet_root="$DATA_ROOT/VerbNet/verbnet-master/verbnet3.4"
if [[ -d "$verbnet_root" ]]; then
  section "VerbNet structured fields"
  echo "members=$(count_matches '<MEMBER[ >]' "$verbnet_root")"
  echo "fn_mapping_attributes=$(count_matches 'fn_mapping=' "$verbnet_root")"
  echo "selectional_restrictions=$(count_matches '<SELRESTR[ >]' "$verbnet_root")"
  echo "syntactic_restrictions=$(count_matches '<SYNRESTR[ >]' "$verbnet_root")"
  echo "semantic_predicates=$(count_matches '<PRED[ >]' "$verbnet_root")"
  echo "semantic_arguments=$(count_matches '<ARG[ >]' "$verbnet_root")"
fi

propbank_root="$DATA_ROOT/PropBank/propbank-frames-main/frames"
if [[ -d "$propbank_root" ]]; then
  section "PropBank structured fields"
  echo "rolesets=$(count_matches '<roleset[ >]' "$propbank_root")"
  echo "aliases=$(count_matches '<alias[ >]' "$propbank_root")"
  echo "roleset_lexlinks=$(count_matches '<lexlink[ >]' "$propbank_root")"
  echo "usage_assertions=$(count_matches '<usage[ >]' "$propbank_root")"
  echo "annotated_example_arguments=$(count_matches '<arg[ >]' "$propbank_root")"
fi

predicate_matrix="$DATA_ROOT/PredicateMatrix.v1.3/PredicateMatrix.v1.3.txt"
if [[ -f "$predicate_matrix" ]]; then
  section "Predicate Matrix row admission"
  awk -F '\t' '
    NR == 1 { next }
    {
      total++
      lang = $1
      pos = $2
      sub(/^[^:]*:/, "", lang)
      sub(/^[^:]*:/, "", pos)
      if (lang == "eng" && pos == "v") english_verbs++
    }
    END {
      rejected = total - english_verbs
      printf "rows=%d english_verb_rows=%d pre_synset_rejected=%d admitted_pct=%.1f\n",
        total, english_verbs, rejected, total ? 100 * english_verbs / total : 0
    }
  ' "$predicate_matrix"
fi

section "missing complementary sources"
for source in FrameBank NomBank OpenEnglishWordNet PreMOn FrameBase; do
  if find "$DATA_ROOT" -maxdepth 2 -iname "*$source*" -print -quit 2>/dev/null | rg -q .; then
    echo "FOUND $source"
  else
    echo "MISS  $source"
  fi
done
