/****************************************************************************
** Meta object code from reading C++ file 'chessengine.h'
**
** Created by: The Qt Meta Object Compiler version 68 (Qt 6.8.3)
**
** WARNING! All changes made in this file will be lost!
*****************************************************************************/

#include "../../../external/cutechess/projects/lib/src/chessengine.h"
#include <QtCore/qmetatype.h>

#include <QtCore/qtmochelpers.h>

#include <memory>


#include <QtCore/qxptype_traits.h>
#if !defined(Q_MOC_OUTPUT_REVISION)
#error "The header file 'chessengine.h' doesn't include <QObject>."
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
struct qt_meta_tag_ZN11ChessEngineE_t {};
} // unnamed namespace


#ifdef QT_MOC_HAS_STRINGDATA
static constexpr auto qt_meta_stringdata_ZN11ChessEngineE = QtMocHelpers::stringData(
    "ChessEngine",
    "go",
    "",
    "quit",
    "kill",
    "onTimeout",
    "onReadyRead",
    "onPingTimeout",
    "onIdleTimeout",
    "pong",
    "emitReady",
    "onProtocolStart",
    "flushWriteBuffer",
    "clearWriteBuffer",
    "onQuitTimeout",
    "onProtocolStartTimeout"
);
#else  // !QT_MOC_HAS_STRINGDATA
#error "qtmochelpers.h not found or too old."
#endif // !QT_MOC_HAS_STRINGDATA

Q_CONSTINIT static const uint qt_meta_data_ZN11ChessEngineE[] = {

 // content:
      12,       // revision
       0,       // classname
       0,    0, // classinfo
      14,   14, // methods
       0,    0, // properties
       0,    0, // enums/sets
       0,    0, // constructors
       0,       // flags
       0,       // signalCount

 // slots: name, argc, parameters, tag, flags, initial metatype offsets
       1,    0,   98,    2, 0x0a,    1 /* Public */,
       3,    0,   99,    2, 0x0a,    2 /* Public */,
       4,    0,  100,    2, 0x0a,    3 /* Public */,
       5,    0,  101,    2, 0x09,    4 /* Protected */,
       6,    0,  102,    2, 0x09,    5 /* Protected */,
       7,    0,  103,    2, 0x09,    6 /* Protected */,
       8,    0,  104,    2, 0x09,    7 /* Protected */,
       9,    1,  105,    2, 0x09,    8 /* Protected */,
       9,    0,  108,    2, 0x29,   10 /* Protected | MethodCloned */,
      11,    0,  109,    2, 0x09,   11 /* Protected */,
      12,    0,  110,    2, 0x09,   12 /* Protected */,
      13,    0,  111,    2, 0x09,   13 /* Protected */,
      14,    0,  112,    2, 0x09,   14 /* Protected */,
      15,    0,  113,    2, 0x09,   15 /* Protected */,

 // slots: parameters
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void, QMetaType::Bool,   10,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void,

       0        // eod
};

Q_CONSTINIT const QMetaObject ChessEngine::staticMetaObject = { {
    QMetaObject::SuperData::link<ChessPlayer::staticMetaObject>(),
    qt_meta_stringdata_ZN11ChessEngineE.offsetsAndSizes,
    qt_meta_data_ZN11ChessEngineE,
    qt_static_metacall,
    nullptr,
    qt_incomplete_metaTypeArray<qt_meta_tag_ZN11ChessEngineE_t,
        // Q_OBJECT / Q_GADGET
        QtPrivate::TypeAndForceComplete<ChessEngine, std::true_type>,
        // method 'go'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'quit'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'kill'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'onTimeout'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'onReadyRead'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'onPingTimeout'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'onIdleTimeout'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'pong'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        QtPrivate::TypeAndForceComplete<bool, std::false_type>,
        // method 'pong'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'onProtocolStart'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'flushWriteBuffer'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'clearWriteBuffer'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'onQuitTimeout'
        QtPrivate::TypeAndForceComplete<void, std::false_type>,
        // method 'onProtocolStartTimeout'
        QtPrivate::TypeAndForceComplete<void, std::false_type>
    >,
    nullptr
} };

void ChessEngine::qt_static_metacall(QObject *_o, QMetaObject::Call _c, int _id, void **_a)
{
    auto *_t = static_cast<ChessEngine *>(_o);
    if (_c == QMetaObject::InvokeMetaMethod) {
        switch (_id) {
        case 0: _t->go(); break;
        case 1: _t->quit(); break;
        case 2: _t->kill(); break;
        case 3: _t->onTimeout(); break;
        case 4: _t->onReadyRead(); break;
        case 5: _t->onPingTimeout(); break;
        case 6: _t->onIdleTimeout(); break;
        case 7: _t->pong((*reinterpret_cast< std::add_pointer_t<bool>>(_a[1]))); break;
        case 8: _t->pong(); break;
        case 9: _t->onProtocolStart(); break;
        case 10: _t->flushWriteBuffer(); break;
        case 11: _t->clearWriteBuffer(); break;
        case 12: _t->onQuitTimeout(); break;
        case 13: _t->onProtocolStartTimeout(); break;
        default: ;
        }
    }
}

const QMetaObject *ChessEngine::metaObject() const
{
    return QObject::d_ptr->metaObject ? QObject::d_ptr->dynamicMetaObject() : &staticMetaObject;
}

void *ChessEngine::qt_metacast(const char *_clname)
{
    if (!_clname) return nullptr;
    if (!strcmp(_clname, qt_meta_stringdata_ZN11ChessEngineE.stringdata0))
        return static_cast<void*>(this);
    return ChessPlayer::qt_metacast(_clname);
}

int ChessEngine::qt_metacall(QMetaObject::Call _c, int _id, void **_a)
{
    _id = ChessPlayer::qt_metacall(_c, _id, _a);
    if (_id < 0)
        return _id;
    if (_c == QMetaObject::InvokeMetaMethod) {
        if (_id < 14)
            qt_static_metacall(this, _c, _id, _a);
        _id -= 14;
    }
    if (_c == QMetaObject::RegisterMethodArgumentMetaType) {
        if (_id < 14)
            *reinterpret_cast<QMetaType *>(_a[0]) = QMetaType();
        _id -= 14;
    }
    return _id;
}
QT_WARNING_POP
