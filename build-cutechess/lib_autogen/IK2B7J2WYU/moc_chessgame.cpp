/****************************************************************************
** Meta object code from reading C++ file 'chessgame.h'
**
** Created by: The Qt Meta Object Compiler version 68 (Qt 6.8.3)
**
** WARNING! All changes made in this file will be lost!
*****************************************************************************/

#include "../../../external/cutechess/projects/lib/src/chessgame.h"
#include <QtCore/qmetatype.h>

#include <QtCore/qtmochelpers.h>

#include <memory>


#include <QtCore/qxptype_traits.h>
#if !defined(Q_MOC_OUTPUT_REVISION)
#error "The header file 'chessgame.h' doesn't include <QObject>."
#elif Q_MOC_OUTPUT_REVISION != 68
#error "This file was generated using the moc from 6.8.3. It"
#error "cannot be used with the include files from this version of Qt."
#error "(The moc has changed too much.)"
#endif

#ifndef Q_CONSTINIT
#define Q_CONSTINIT
#endif

QT_WARNING_PUSH
QT_WARNING_DISABLE_DEPRECATED
QT_WARNING_DISABLE_GCC("-Wuseless-cast")
namespace {
struct qt_meta_tag_ZN9ChessGameE_t {};
} // unnamed namespace


#ifdef QT_MOC_HAS_STRINGDATA
static constexpr auto qt_meta_stringdata_ZN9ChessGameE = QtMocHelpers::stringData(
    "ChessGame",
    "humanEnabled",
    "",
    "fenChanged",
    "fenString",
    "moveMade",
    "Chess::GenericMove",
    "move",
    "sanString",
    "comment",
    "moveChanged",
    "ply",
    "scoreChanged",
    "score",
    "initialized",
    "ChessGame*",
    "game",
    "started",
    "finished",
    "Chess::Result",
    "result",
    "startFailed",
    "playersReady",
    "start",
    "pause",
    "resume",
    "stop",
    "emitMoveChanged",
    "kill",
    "emitStartFailed",
    "onMoveMade",
    "Chess::Move",
    "onAdjudication",
    "onResignation",
    "startGame",
    "startTurn",
    "finish",
    "onResultClaim",
    "onPlayerReady",
    "syncPlayers",
    "pauseThread"
);
#else  // !QT_MOC_HAS_STRINGDATA
#error "qtmochelpers.h not found or too old."
#endif // !QT_MOC_HAS_STRINGDATA

Q_CONSTINIT static const uint qt_meta_data_ZN9ChessGameE[] = {

 // content:
      12,       // revision
       0,       // classname
       0,    0, // classinfo
      32,   14, // methods
       0,    0, // properties
       0,    0, // enums/sets
       0,    0, // constructors
       0,       // flags
      15,       // signalCount

 // signals: name, argc, parameters, tag, flags, initial metatype offsets
       1,    1,  206,    2, 0x06,    1 /* Public */,
       3,    1,  209,    2, 0x06,    3 /* Public */,
       5,    3,  212,    2, 0x06,    5 /* Public */,
      10,    4,  219,    2, 0x06,    9 /* Public */,
      12,    2,  228,    2, 0x06,   14 /* Public */,
      14,    1,  233,    2, 0x06,   17 /* Public */,
      14,    0,  236,    2, 0x26,   19 /* Public | MethodCloned */,
      17,    1,  237,    2, 0x06,   20 /* Public */,
      17,    0,  240,    2, 0x26,   22 /* Public | MethodCloned */,
      18,    2,  241,    2, 0x06,   23 /* Public */,
      18,    1,  246,    2, 0x26,   26 /* Public | MethodCloned */,
      18,    0,  249,    2, 0x26,   28 /* Public | MethodCloned */,
      21,    1,  250,    2, 0x06,   29 /* Public */,
      21,    0,  253,    2, 0x26,   31 /* Public | MethodCloned */,
      22,    0,  254,    2, 0x06,   32 /* Public */,

 // slots: name, argc, parameters, tag, flags, initial metatype offsets
      23,    0,  255,    2, 0x0a,   33 /* Public */,
      24,    0,  256,    2, 0x0a,   34 /* Public */,
      25,    0,  257,    2, 0x0a,   35 /* Public */,
      26,    1,  258,    2, 0x0a,   36 /* Public */,
      26,    0,  261,    2, 0x2a,   38 /* Public | MethodCloned */,
      28,    0,  262,    2, 0x0a,   39 /* Public */,
      29,    0,  263,    2, 0x0a,   40 /* Public */,
      30,    1,  264,    2, 0x0a,   41 /* Public */,
      32,    1,  267,    2, 0x0a,   43 /* Public */,
      33,    1,  270,    2, 0x0a,   45 /* Public */,
      34,    0,  273,    2, 0x08,   47 /* Private */,
      35,    0,  274,    2, 0x08,   48 /* Private */,
      36,    0,  275,    2, 0x08,   49 /* Private */,
      37,    1,  276,    2, 0x08,   50 /* Private */,
      38,    0,  279,    2, 0x08,   52 /* Private */,
      39,    0,  280,    2, 0x08,   53 /* Private */,
      40,    0,  281,    2, 0x08,   54 /* Private */,

 // signals: parameters
    QMetaType::Void, QMetaType::Bool,    2,
    QMetaType::Void, QMetaType::QString,    4,
    QMetaType::Void, 0x80000000 | 6, QMetaType::QString, QMetaType::QString,    7,    8,    9,
    QMetaType::Void, QMetaType::Int, 0x80000000 | 6, QMetaType::QString, QMetaType::QString,   11,    7,    8,    9,
    QMetaType::Void, QMetaType::Int, QMetaType::Int,   11,   13,
    QMetaType::Void, 0x80000000 | 15,   16,
    QMetaType::Void,
    QMetaType::Void, 0x80000000 | 15,   16,
    QMetaType::Void,
    QMetaType::Void, 0x80000000 | 15, 0x80000000 | 19,   16,   20,
    QMetaType::Void, 0x80000000 | 15,   16,
    QMetaType::Void,
    QMetaType::Void, 0x80000000 | 15,   16,
    QMetaType::Void,
    QMetaType::Void,

 // slots: parameters
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void, QMetaType::Bool,   27,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void, 0x80000000 | 31,    7,
    QMetaType::Void, 0x80000000 | 19,   20,
    QMetaType::Void, 0x80000000 | 19,   20,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void, 0x80000000 | 19,   20,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,

       0        // eod
};

Q_CONSTINIT const QMetaObject ChessGame::staticMetaObject = { {
    QMetaObject::SuperData::link<QObject::staticMetaObject>(),
    qt_meta_stringdata_ZN9ChessGameE.offsetsAndSizes,
    qt_meta_data_ZN9ChessGameE,
    qt_static_metacall,
    nullptr,
    qt_incomplete_metaTypeArray<qt_meta_tag_ZN9ChessGameE_t,
        // Q_OBJECT / Q_GADGET
        QtPrivate::TypeAndForceComplete<ChessGame, std::true_type>,
        // method 'humanEnabled'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<bool, std::false_type>,
        // method 'fenChanged'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<const QString &, std::false_type>,
        // method 'moveMade'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<const Chess::GenericMove &, std::false_type>,
        QtPrivate::TypeAndForceComplete<const QString &, std::false_type>,
        QtPrivate::TypeAndForceComplete<const QString &, std::false_type>,
        // method 'moveChanged'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<int, std::false_type>,
        QtPrivate::TypeAndForceComplete<const Chess::GenericMove &, std::false_type>,
        QtPrivate::TypeAndForceComplete<const QString &, std::false_type>,
        QtPrivate::TypeAndForceComplete<const QString &, std::false_type>,
        // method 'scoreChanged'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<int, std::false_type>,
        QtPrivate::TypeAndForceComplete<int, std::false_type>,
        // method 'initialized'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<ChessGame *, std::false_type>,
        // method 'initialized'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'started'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<ChessGame *, std::false_type>,
        // method 'started'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'finished'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<ChessGame *, std::false_type>,
        QtPrivate::TypeAndForceComplete<Chess::Result, std::false_type>,
        // method 'finished'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<ChessGame *, std::false_type>,
        // method 'finished'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'startFailed'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<ChessGame *, std::false_type>,
        // method 'startFailed'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'playersReady'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'start'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'pause'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'resume'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'stop'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<bool, std::false_type>,
        // method 'stop'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'kill'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'emitStartFailed'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'onMoveMade'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<const Chess::Move &, std::false_type>,
        // method 'onAdjudication'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<const Chess::Result &, std::false_type>,
        // method 'onResignation'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<const Chess::Result &, std::false_type>,
        // method 'startGame'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'startTurn'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'finish'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'onResultClaim'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<const Chess::Result &, std::false_type>,
        // method 'onPlayerReady'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'syncPlayers'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'pauseThread'
        QtPrivate::TypeAndForceComplete<void, std::false_type>
    >,
    nullptr
} };

void ChessGame::qt_static_metacall(QObject *_o, QMetaObject::Call _c, int _id, void **_a)
{
    auto *_t = static_cast<ChessGame *>(_o);
    if (_c == QMetaObject::InvokeMetaMethod) {
        switch (_id) {
        case 0: _t->humanEnabled((*reinterpret_cast< std::add_pointer_t<bool>>(_a[1]))); break;
        case 1: _t->fenChanged((*reinterpret_cast< std::add_pointer_t<QString>>(_a[1]))); break;
        case 2: _t->moveMade((*reinterpret_cast< std::add_pointer_t<Chess::GenericMove>>(_a[1])),(*reinterpret_cast< std::add_pointer_t<QString>>(_a[2])),(*reinterpret_cast< std::add_pointer_t<QString>>(_a[3]))); break;
        case 3: _t->moveChanged((*reinterpret_cast< std::add_pointer_t<int>>(_a[1])),(*reinterpret_cast< std::add_pointer_t<Chess::GenericMove>>(_a[2])),(*reinterpret_cast< std::add_pointer_t<QString>>(_a[3])),(*reinterpret_cast< std::add_pointer_t<QString>>(_a[4]))); break;
        case 4: _t->scoreChanged((*reinterpret_cast< std::add_pointer_t<int>>(_a[1])),(*reinterpret_cast< std::add_pointer_t<int>>(_a[2]))); break;
        case 5: _t->initialized((*reinterpret_cast< std::add_pointer_t<ChessGame*>>(_a[1]))); break;
        case 6: _t->initialized(); break;
        case 7: _t->started((*reinterpret_cast< std::add_pointer_t<ChessGame*>>(_a[1]))); break;
        case 8: _t->started(); break;
        case 9: _t->finished((*reinterpret_cast< std::add_pointer_t<ChessGame*>>(_a[1])),(*reinterpret_cast< std::add_pointer_t<Chess::Result>>(_a[2]))); break;
        case 10: _t->finished((*reinterpret_cast< std::add_pointer_t<ChessGame*>>(_a[1]))); break;
        case 11: _t->finished(); break;
        case 12: _t->startFailed((*reinterpret_cast< std::add_pointer_t<ChessGame*>>(_a[1]))); break;
        case 13: _t->startFailed(); break;
        case 14: _t->playersReady(); break;
        case 15: _t->start(); break;
        case 16: _t->pause(); break;
        case 17: _t->resume(); break;
        case 18: _t->stop((*reinterpret_cast< std::add_pointer_t<bool>>(_a[1]))); break;
        case 19: _t->stop(); break;
        case 20: _t->kill(); break;
        case 21: _t->emitStartFailed(); break;
        case 22: _t->onMoveMade((*reinterpret_cast< std::add_pointer_t<Chess::Move>>(_a[1]))); break;
        case 23: _t->onAdjudication((*reinterpret_cast< std::add_pointer_t<Chess::Result>>(_a[1]))); break;
        case 24: _t->onResignation((*reinterpret_cast< std::add_pointer_t<Chess::Result>>(_a[1]))); break;
        case 25: _t->startGame(); break;
        case 26: _t->startTurn(); break;
        case 27: _t->finish(); break;
        case 28: _t->onResultClaim((*reinterpret_cast< std::add_pointer_t<Chess::Result>>(_a[1]))); break;
        case 29: _t->onPlayerReady(); break;
        case 30: _t->syncPlayers(); break;
        case 31: _t->pauseThread(); break;
        default: ;
        }
    }
    if (_c == QMetaObject::RegisterMethodArgumentMetaType) {
        switch (_id) {
        default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
        case 2:
            switch (*reinterpret_cast<int*>(_a[1])) {
            default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
            case 0:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< Chess::GenericMove >(); break;
            }
            break;
        case 3:
            switch (*reinterpret_cast<int*>(_a[1])) {
            default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
            case 1:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< Chess::GenericMove >(); break;
            }
            break;
        case 5:
            switch (*reinterpret_cast<int*>(_a[1])) {
            default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
            case 0:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< ChessGame* >(); break;
            }
            break;
        case 7:
            switch (*reinterpret_cast<int*>(_a[1])) {
            default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
            case 0:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< ChessGame* >(); break;
            }
            break;
        case 9:
            switch (*reinterpret_cast<int*>(_a[1])) {
            default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
            case 1:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< Chess::Result >(); break;
            case 0:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< ChessGame* >(); break;
            }
            break;
        case 10:
            switch (*reinterpret_cast<int*>(_a[1])) {
            default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
            case 0:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< ChessGame* >(); break;
            }
            break;
        case 12:
            switch (*reinterpret_cast<int*>(_a[1])) {
            default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
            case 0:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< ChessGame* >(); break;
            }
            break;
        case 22:
            switch (*reinterpret_cast<int*>(_a[1])) {
            default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
            case 0:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< Chess::Move >(); break;
            }
            break;
        case 23:
            switch (*reinterpret_cast<int*>(_a[1])) {
            default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
            case 0:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< Chess::Result >(); break;
            }
            break;
        case 24:
            switch (*reinterpret_cast<int*>(_a[1])) {
            default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
            case 0:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< Chess::Result >(); break;
            }
            break;
        case 28:
            switch (*reinterpret_cast<int*>(_a[1])) {
            default: *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType(); break;
            case 0:
                *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType::fromType< Chess::Result >(); break;
            }
            break;
        }
    }
    if (_c == QMetaObject::IndexOfMethod) {
        int *result = reinterpret_cast<int *>(_a[0]);
        {
            using _q_method_type = void (ChessGame::*)(bool );
            if (_q_method_type _q_method = &ChessGame::humanEnabled; *reinterpret_cast<_q_method_type *>(_a[1]) == _q_method) {
                *result = 0;
                return;
            }
        }
        {
            using _q_method_type = void (ChessGame::*)(const QString & );
            if (_q_method_type _q_method = &ChessGame::fenChanged; *reinterpret_cast<_q_method_type *>(_a[1]) == _q_method) {
                *result = 1;
                return;
            }
        }
        {
            using _q_method_type = void (ChessGame::*)(const Chess::GenericMove & , const QString & , const QString & );
            if (_q_method_type _q_method = &ChessGame::moveMade; *reinterpret_cast<_q_method_type *>(_a[1]) == _q_method) {
                *result = 2;
                return;
            }
        }
        {
            using _q_method_type = void (ChessGame::*)(int , const Chess::GenericMove & , const QString & , const QString & );
            if (_q_method_type _q_method = &ChessGame::moveChanged; *reinterpret_cast<_q_method_type *>(_a[1]) == _q_method) {
                *result = 3;
                return;
            }
        }
        {
            using _q_method_type = void (ChessGame::*)(int , int );
            if (_q_method_type _q_method = &ChessGame::scoreChanged; *reinterpret_cast<_q_method_type *>(_a[1]) == _q_method) {
                *result = 4;
                return;
            }
        }
        {
            using _q_method_type = void (ChessGame::*)(ChessGame * );
            if (_q_method_type _q_method = &ChessGame::initialized; *reinterpret_cast<_q_method_type *>(_a[1]) == _q_method) {
                *result = 5;
                return;
            }
        }
        {
            using _q_method_type = void (ChessGame::*)(ChessGame * );
            if (_q_method_type _q_method = &ChessGame::started; *reinterpret_cast<_q_method_type *>(_a[1]) == _q_method) {
                *result = 7;
                return;
            }
        }
        {
            using _q_method_type = void (ChessGame::*)(ChessGame * , Chess::Result );
            if (_q_method_type _q_method = &ChessGame::finished; *reinterpret_cast<_q_method_type *>(_a[1]) == _q_method) {
                *result = 9;
                return;
            }
        }
        {
            using _q_method_type = void (ChessGame::*)(ChessGame * );
            if (_q_method_type _q_method = &ChessGame::startFailed; *reinterpret_cast<_q_method_type *>(_a[1]) == _q_method) {
                *result = 12;
                return;
            }
        }
        {
            using _q_method_type = void (ChessGame::*)();
            if (_q_method_type _q_method = &ChessGame::playersReady; *reinterpret_cast<_q_method_type *>(_a[1]) == _q_method) {
                *result = 14;
                return;
            }
        }
    }
}

const QMetaObject *ChessGame::metaObject() const
{
    return QObject::d_ptr->metaObject ? QObject::d_ptr->dynamicMetaObject() : &staticMetaObject;
}

void *ChessGame::qt_metacast(const char *_clname)
{
    if (!_clname) return nullptr;
    if (!strcmp(_clname, qt_meta_stringdata_ZN9ChessGameE.stringdata0))
        return static_cast<void*>(this);
    return QObject::qt_metacast(_clname);
}

int ChessGame::qt_metacall(QMetaObject::Call _c, int _id, void **_a)
{
    _id = QObject::qt_metacall(_c, _id, _a);
    if (_id < 0)
        return _id;
    if (_c == QMetaObject::InvokeMetaMethod) {
        if (_id < 32)
            qt_static_metacall(this, _c, _id, _a);
        _id -= 32;
    }
    if (_c == QMetaObject::RegisterMethodArgumentMetaType) {
        if (_id < 32)
            qt_static_metacall(this, _c, _id, _a);
        _id -= 32;
    }
    return _id;
}

// SIGNAL 0
void ChessGame::humanEnabled(bool _t1)
{
    void *_a[] = { nullptr, const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t1))) };
    QMetaObject::activate(this, &staticMetaObject, 0, _a);
}

// SIGNAL 1
void ChessGame::fenChanged(const QString & _t1)
{
    void *_a[] = { nullptr, const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t1))) };
    QMetaObject::activate(this, &staticMetaObject, 1, _a);
}

// SIGNAL 2
void ChessGame::moveMade(const Chess::GenericMove & _t1, const QString & _t2, const QString & _t3)
{
    void *_a[] = { nullptr, const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t1))), const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t2))), const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t3))) };
    QMetaObject::activate(this, &staticMetaObject, 2, _a);
}

// SIGNAL 3
void ChessGame::moveChanged(int _t1, const Chess::GenericMove & _t2, const QString & _t3, const QString & _t4)
{
    void *_a[] = { nullptr, const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t1))), const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t2))), const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t3))), const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t4))) };
    QMetaObject::activate(this, &staticMetaObject, 3, _a);
}

// SIGNAL 4
void ChessGame::scoreChanged(int _t1, int _t2)
{
    void *_a[] = { nullptr, const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t1))), const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t2))) };
    QMetaObject::activate(this, &staticMetaObject, 4, _a);
}

// SIGNAL 5
void ChessGame::initialized(ChessGame * _t1)
{
    void *_a[] = { nullptr, const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t1))) };
    QMetaObject::activate(this, &staticMetaObject, 5, _a);
}

// SIGNAL 7
void ChessGame::started(ChessGame * _t1)
{
    void *_a[] = { nullptr, const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t1))) };
    QMetaObject::activate(this, &staticMetaObject, 7, _a);
}

// SIGNAL 9
void ChessGame::finished(ChessGame * _t1, Chess::Result _t2)
{
    void *_a[] = { nullptr, const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t1))), const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t2))) };
    QMetaObject::activate(this, &staticMetaObject, 9, _a);
}

// SIGNAL 12
void ChessGame::startFailed(ChessGame * _t1)
{
    void *_a[] = { nullptr, const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t1))) };
    QMetaObject::activate(this, &staticMetaObject, 12, _a);
}

// SIGNAL 14
void ChessGame::playersReady()
{
    QMetaObject::activate(this, &staticMetaObject, 14, nullptr);
}
QT_WARNING_POP
