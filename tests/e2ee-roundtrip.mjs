import {
  createApplicationMessage,
  createCommit,
  createGroup,
  decodeGroupState,
  decodeMlsMessage,
  defaultCapabilities,
  defaultLifetime,
  emptyPskIndex,
  encodeGroupState,
  encodeMlsMessage,
  generateKeyPackage,
  getCiphersuiteFromName,
  getCiphersuiteImpl,
  joinGroup,
  makePskIndex,
  processPrivateMessage,
} from 'ts-mls';

const encoder = new TextEncoder();
const decoder = new TextDecoder();
const suite = await getCiphersuiteImpl(
  getCiphersuiteFromName('MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519'),
);
const alicePackage = await generateKeyPackage(
  { credentialType: 'basic', identity: encoder.encode('{"u":"alice","d":"one"}') },
  defaultCapabilities(),
  defaultLifetime,
  [],
  suite,
);
const bobPackage = await generateKeyPackage(
  { credentialType: 'basic', identity: encoder.encode('{"u":"bob","d":"two"}') },
  defaultCapabilities(),
  defaultLifetime,
  [],
  suite,
);

let alice = await createGroup(
  encoder.encode('channel'),
  alicePackage.publicPackage,
  alicePackage.privatePackage,
  [],
  suite,
);
const add = await createCommit(
  { state: alice, cipherSuite: suite },
  {
    extraProposals: [{ proposalType: 'add', add: { keyPackage: bobPackage.publicPackage } }],
    ratchetTreeExtension: true,
  },
);
alice = add.newState;

const encodedWelcome = encodeMlsMessage({ welcome: add.welcome, wireformat: 'mls_welcome', version: 'mls10' });
const decodedWelcome = decodeMlsMessage(encodedWelcome, 0)?.[0];
if (!decodedWelcome || decodedWelcome.wireformat !== 'mls_welcome') throw new Error('Welcome round trip failed.');
let bob = await joinGroup(
  decodedWelcome.welcome,
  bobPackage.publicPackage,
  bobPackage.privatePackage,
  emptyPskIndex,
  suite,
);

const saved = encodeGroupState(alice);
const decoded = decodeGroupState(saved, 0)?.[0];
if (!decoded) throw new Error('State round trip failed.');
alice = { ...decoded, clientConfig: alice.clientConfig };

const outgoing = await createApplicationMessage(alice, encoder.encode('hello'), suite);
const wire = encodeMlsMessage({
  privateMessage: outgoing.privateMessage,
  wireformat: 'mls_private_message',
  version: 'mls10',
});
const incoming = decodeMlsMessage(wire, 0)?.[0];
if (!incoming || incoming.wireformat !== 'mls_private_message') throw new Error('Message round trip failed.');
const received = await processPrivateMessage(bob, incoming.privateMessage, makePskIndex(bob, {}), suite);
if (received.kind !== 'applicationMessage' || decoder.decode(received.message) !== 'hello') {
  throw new Error('The second group member could not decrypt the message.');
}
bob = received.newState;

if (alice.groupContext.epoch !== bob.groupContext.epoch) throw new Error('Group epochs do not match.');
console.log(`MLS round trip passed at epoch ${alice.groupContext.epoch}.`);
