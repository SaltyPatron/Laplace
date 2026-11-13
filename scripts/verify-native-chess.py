import ctypes as c, pathlib, sys, io, json, argparse, hashlib, os
parser=argparse.ArgumentParser(description="Compare native chess transitions with the pinned independent rules provider.")
for name in ["library","pgn","provider-cache","output"]: parser.add_argument("--"+name,required=True,type=pathlib.Path)
args=parser.parse_args()
root=pathlib.Path(__file__).resolve().parents[1]; sys.path.insert(0,str(root/'scripts'))
from lib.chess_pgn import load_provider
chess,pgn,_=load_provider(args.provider_cache)
class State(c.Structure):
 _fields_=[('squares',c.c_int8*128),('pockets',c.c_uint16*10),('promoted',c.c_uint64),('white',c.c_int32),('castle',c.c_int32),('ep',c.c_int32),('halfmove',c.c_int32),('fullmove',c.c_int32),('rook_files',c.c_int32*4),('drops',c.c_int32),('checks',c.c_uint32*2),('check_target',c.c_uint32),('winning_squares',c.c_uint64)]
class Move(c.Structure):
 _fields_=[('from_',c.c_int32),('to',c.c_int32),('promotion',c.c_int32),('flags',c.c_int32)]
def sq(n): return (n//8)*16+n%8
def encode(b):
 s=State(); s.white=int(b.turn); s.ep=-1 if b.ep_square is None else sq(b.ep_square); s.halfmove=b.halfmove_clock; s.fullmove=b.fullmove_number; s.promoted=b.promoted
 s.rook_files[:]=[7,0,7,0]; s.drops=int(hasattr(b,'pockets'))
 if hasattr(b,'remaining_checks'): s.check_target=3; s.checks[:]=[3-b.remaining_checks[True],3-b.remaining_checks[False]]
 if b.uci_variant=='kingofthehill': s.winning_squares=chess.BB_CENTER
 for square,piece in b.piece_map().items(): s.squares[sq(square)]=piece.piece_type*(1 if piece.color else -1)
 for color,base in [(True,0),(False,2)]:
  if b.has_kingside_castling_rights(color):
   s.castle|=1<<base; s.rook_files[base]=max(chess.scan_forward(b.castling_rights & b.rooks & b.occupied_co[color]))%8
  if b.has_queenside_castling_rights(color):
   s.castle|=1<<(base+1); s.rook_files[base+1]=min(chess.scan_forward(b.castling_rights & b.rooks & b.occupied_co[color]))%8
  if s.drops:
   for t in range(1,6): s.pockets[(0 if color else 5)+t-1]=b.pockets[color].count(t)
 return s
def native_uci(m):
 alg=lambda n:chr(97+(n&7))+str((n>>4)+1)
 if m.flags&4 and b.chess960:return alg(m.from_)+alg((0 if s.white else 112)+s.rook_files[(0 if s.white else 2)+(0 if m.to%16==6 else 1)])
 if m.flags&16:return ' PNBRQK'[m.promotion]+'@'+alg(m.to)
 return alg(m.from_)+alg(m.to)+(' pnbrqk'[m.promotion] if m.flags&8 else '')
dll_directory = os.add_dll_directory(str(args.library.resolve().parent)) if os.name == 'nt' else None
dll=c.CDLL(str(args.library.resolve()))
dll.laplace_chess_moves.argtypes=[c.POINTER(State),c.c_int,c.POINTER(Move),c.c_size_t]; dll.laplace_chess_moves.restype=c.c_size_t
dll.laplace_chess_resolve.argtypes=[c.POINTER(State),c.c_char_p,c.POINTER(Move)]
dll.laplace_chess_apply.argtypes=[c.POINTER(State),c.POINTER(Move)]
source=args.pgn; stream=io.StringIO(source.read_text()); games=plies=drops=0
while (game:=pgn.read_game(stream)) is not None:
 assert not game.errors,game.errors
 b=game.board(); s=encode(b)
 for i,m in enumerate(game.mainline_moves()):
  arr=(Move*2050)(); n=dll.laplace_chess_moves(c.byref(s),1,arr,len(arr)); assert n<=len(arr)
  expected=set(x.uci() for x in b.legal_moves); got=set(native_uci(x) for x in arr[:n]); assert expected==got,(games,i,game.headers.get('Link'),sorted(expected-got),sorted(got-expected))
  san=b.san(m); nm=Move(); assert dll.laplace_chess_resolve(c.byref(s),san.encode(),c.byref(nm))==0,(games,i,san)
  assert native_uci(nm)==m.uci(),(games,i,san,native_uci(nm),m.uci())
  assert dll.laplace_chess_apply(c.byref(s),c.byref(nm))==0
  b.push(m); want=encode(b)
  for k in ['squares','pockets','checks']:
   assert list(getattr(s,k))==list(getattr(want,k)),(games,i,san,k)
  for k in ['promoted','white','castle','ep','halfmove','fullmove','drops','check_target','winning_squares']:
   assert getattr(s,k)==getattr(want,k),(games,i,san,k,getattr(s,k),getattr(want,k))
  plies+=1; drops+=bool(nm.flags&16)
 games+=1
report={'source_sha256':hashlib.sha256(source.read_bytes()).hexdigest(),'games':games,'plies':plies,'drops':drops,'verification':'Native legal move sets, SAN resolution, every board, pockets, promoted provenance and counters agree with pinned independent python-chess rules provider.'}
print(json.dumps(report,indent=2)); args.output.write_text(json.dumps(report,indent=2))
