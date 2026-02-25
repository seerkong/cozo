import { createRouter, createWebHistory } from "vue-router";
import OntologyDemo from "./pages/OntologyDemo.vue";
import PermissionDemo from "./pages/PermissionDemo.vue";
import GovernanceDemo from "./pages/GovernanceDemo.vue";
import SchemaVersioningDemo from "./pages/SchemaVersioningDemo.vue";

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/", redirect: "/ontology" },
    { path: "/ontology", name: "ontology", component: OntologyDemo },
    { path: "/permission", name: "permission", component: PermissionDemo },
    { path: "/governance", name: "governance", component: GovernanceDemo },
    { path: "/schema", name: "schema", component: SchemaVersioningDemo },
  ],
});

export default router;
